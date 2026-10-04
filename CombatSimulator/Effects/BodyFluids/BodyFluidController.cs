// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Diagnostics;
using System.Numerics;
using CombatSimulator.Core;
using CombatSimulator.Animation;
using CombatSimulator.Rendering.WorldGeometry;
using CombatSimulator.Effects.BodyFluids.Surface;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace CombatSimulator.Effects.BodyFluids;

/// <summary>One bounded, read-only cosmetic simulation. Never writes a character or camera.</summary>
public sealed class BodyFluidController : IDisposable
{
    private const float Step = 1f / 60f;
    private const float DropVolume = 0.02e-6f; // 0.02 ml, world units treated as metres.
    private const float FilamentSeedVolume = 0.003e-6f;
    private const float FedStrandRadius = 0.00045f;
    private const int NodeCount = 12;
    private static readonly Vector3 Gravity = new(0, -9.81f, 0);
    private readonly object gate = new();
    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly BoneTransformService bones;
    private readonly CharacterFluidSurface surface;
    private readonly WorldGeometryRenderer worldRenderer;
    private readonly WorldGeometryLayer renderer;
    private readonly WorldGeometryBuilder geometry = new();
    private readonly Drop[] drops = new Drop[64];
    private readonly Deposit[] deposits = new Deposit[128];
    private readonly Filament[] filaments = new Filament[8];
    private readonly Puddle[] puddles = new Puddle[16];
    private float accumulatedTime, reservoir, timedEmission;
    private bool manualEmission, enabled, disposed;
    private int terrainBudget, contactBudget;
    private double emittedVolume, expiredVolume;
    private long steps, poseFrames;
    private long frameworkFrame, capturedFrame, previousPoseTicks;
    private nint actorIdentity;
    private ulong objectIdentity;
    private uint territoryIdentity;
    private float visualProbeTime;
    private Vector3 visualProbeOrigin;
    public double LastCpuMilliseconds { get; private set; }
    public bool Emitting => manualEmission || timedEmission > 0;
    public string SurfaceStatus => surface.Status;
    public string RenderStatus => renderer.Status;

    private struct Drop
    {
        public bool Active;
        public Vector3 Position, Velocity;
        public float Volume, Age;
    }

    private struct Deposit
    {
        public bool Active, Trail;
        public FluidSurfaceAnchor Anchor;
        public Vector3 RelativeVelocity;
        public float Volume, Age, TrailTimer;
    }

    private sealed class Filament
    {
        public bool Active, TipAttached, Fed;
        public FluidSurfaceAnchor TipAnchor;
        public float Volume, Age, DrainRate;
        public readonly Vector3[] Points = new Vector3[NodeCount];
        public readonly Vector3[] Previous = new Vector3[NodeCount];
        public readonly Vector3[] Velocities = new Vector3[NodeCount];
        public readonly float[] Rest = new float[NodeCount - 1];
        public readonly float[] Lambda = new float[NodeCount - 1];
    }

    private sealed class Puddle
    {
        public bool Active;
        public Vector3 Center, Normal;
        public float Volume, Age, Radius;
        public bool NeedsSamples;
        public readonly Vector3[] Boundary = new Vector3[8];
        public readonly bool[] BoundaryValid = new bool[8];
    }

    public BodyFluidController(Configuration config, BoneTransformService bones,
        WorldGeometryRenderer worldRenderer, IPluginLog log)
    {
        this.config = config;
        this.bones = bones;
        this.log = log;
        this.worldRenderer = worldRenderer;
        surface = new CharacterFluidSurface(bones);
        renderer = worldRenderer.CreateLayer("Saliva");
        for (var i = 0; i < filaments.Length; i++) filaments[i] = new Filament();
        for (var i = 0; i < puddles.Length; i++) puddles[i] = new Puddle();
        bones.OnPosePrepared += OnPosePrepared;
    }

    public void Tick(float dt)
    {
        lock (gate)
        {
            if (disposed) return;
            if (enabled != config.BodyFluidsEnabled)
            {
                enabled = config.BodyFluidsEnabled;
                config.ClampBodyFluids();
                renderer.SetEnabled(enabled);
                if (!enabled) ClearCore();
            }
            if (!enabled) return;
            if (renderer.IsSuspended) return;
            var player = Services.ObjectTable.LocalPlayer;
            if (!Services.ClientState.IsLoggedIn || player == null)
            {
                ClearCore();
                return;
            }
            if (!Emitting && !HasLiquid()) return;
            frameworkFrame++;
            actorIdentity = player.Address;
            objectIdentity = player.GameObjectId;
            territoryIdentity = Services.ClientState.TerritoryType;
            surface.MouthOffset = new Vector3(0, config.BodyFluidMouthHeightOffset, config.BodyFluidMouthForwardOffset);
            if (visualProbeTime <= 0)
                surface.UpdateActor(player.Address, player.GameObjectId, Services.ClientState.TerritoryType);
            accumulatedTime = MathF.Min(accumulatedTime + Math.Clamp(dt, 0, 0.1f), Step * 2);
        }
    }

    public void Start()
    {
        lock (gate)
        {
            if (disposed || !Services.ClientState.IsLoggedIn) return;
            if (visualProbeTime > 0) ClearCore();
            config.BodyFluidsEnabled = true;
            config.ClampBodyFluids();
            enabled = true;
            renderer.SetEnabled(true);
            if (renderer.IsSuspended) return;
            manualEmission = true;
        }
    }

    public void StopEmission()
    {
        lock (gate) { manualEmission = false; timedEmission = 0; }
    }

    public void OnPlayerKo()
    {
        lock (gate)
            if (!disposed && config.BodyFluidsEnabled && config.BodyFluidsOnPlayerKo)
                timedEmission = 10f;
    }

    public void BeginProbe()
    {
        lock (gate) worldRenderer.BeginProbe();
    }

    /// <summary>Finite world-space geometry proof before any skin/contact simulation is enabled.</summary>
    public void BeginVisualProbe()
    {
        lock (gate)
        {
            var player = Services.ObjectTable.LocalPlayer;
            if (disposed || !Services.ClientState.IsLoggedIn || player == null) return;
            ClearCore();
            enabled = config.BodyFluidsEnabled = true;
            renderer.SetEnabled(true);
            if (renderer.IsSuspended) return;
            visualProbeOrigin = bones.GetBoneWorldPos(player.Address, "j_kao") ?? (player.Position + Vector3.UnitY * 1.3f);
            visualProbeTime = 12f;
        }
    }

    public void Clear()
    {
        lock (gate) ClearCore();
    }

    private void ClearCore()
    {
        manualEmission = false;
        timedEmission = accumulatedTime = reservoir = 0;
        visualProbeTime = 0;
        Array.Clear(drops);
        Array.Clear(deposits);
        foreach (var filament in filaments) filament.Active = false;
        foreach (var puddle in puddles) puddle.Active = false;
        emittedVolume = expiredVolume = 0;
        steps = poseFrames = 0;
        actorIdentity = 0;
        objectIdentity = 0;
        previousPoseTicks = frameworkFrame = capturedFrame = 0;
        surface.Clear();
        geometry.Reset();
        renderer.Clear();
    }

    private bool HasLiquid()
    {
        if (visualProbeTime > 0) return true;
        if (reservoir > 0) return true;
        foreach (ref var drop in drops.AsSpan()) if (drop.Active) return true;
        foreach (ref var deposit in deposits.AsSpan()) if (deposit.Active) return true;
        foreach (var filament in filaments) if (filament.Active) return true;
        foreach (var puddle in puddles) if (puddle.Active) return true;
        return false;
    }

    private void OnPosePrepared()
    {
        lock (gate)
        {
            if (disposed || !enabled || renderer.IsSuspended || accumulatedTime <= 0 || capturedFrame == frameworkFrame) return;
            var player = Services.ObjectTable.LocalPlayer;
            if (!Services.ClientState.IsLoggedIn || player == null || player.Address != actorIdentity ||
                player.GameObjectId != objectIdentity || Services.ClientState.TerritoryType != territoryIdentity)
            { ClearCore(); return; }
            capturedFrame = frameworkFrame;
            var started = Stopwatch.GetTimestamp();
            try
            {
                var poseDelta = previousPoseTicks == 0 ? Step : (float)Stopwatch.GetElapsedTime(previousPoseTicks, started).TotalSeconds;
                previousPoseTicks = started;
                if (visualProbeTime > 0)
                {
                    visualProbeTime = MathF.Max(0, visualProbeTime - Math.Clamp(poseDelta, 0, 0.1f));
                    accumulatedTime = 0;
                    geometry.Reset();
                    if (visualProbeTime > 8)
                    {
                        geometry.AddSurfaceTriangle(new Vector3(-0.8f, 0.8f, 0.5f), new Vector3(-0.5f, 0.8f, 0.5f),
                            new Vector3(-0.65f, 0.5f, 0.5f), Vector3.UnitZ, new Vector4(1, 0, 1, 1));
                    }
                    else if (visualProbeTime > 0)
                    {
                        geometry.AddEllipsoid(visualProbeOrigin + Vector3.UnitX * 0.2f, 0.025f, new Vector4(1, 0.12f, 0.12f, 0.85f));
                        geometry.AddEllipsoid(visualProbeOrigin, 0.025f, new Vector4(0.12f, 0.25f, 1, 0.85f));
                        geometry.AddEllipsoid(visualProbeOrigin - Vector3.UnitX * 0.2f, 0.025f, new Vector4(0.12f, 1, 0.2f, 0.85f));
                    }
                    renderer.SubmitFrame(geometry.Vertices, testSceneDepth: visualProbeTime <= 4, clipSpace: visualProbeTime > 8, diagnostic: true);
                    if (visualProbeTime == 0) renderer.Clear();
                    return;
                }
                if (!surface.CapturePose(poseDelta))
                {
                    accumulatedTime = 0;
                    renderer.Clear();
                    return;
                }
                poseFrames++;
                var count = 0;
                while (accumulatedTime + 1e-6f >= Step && count++ < 2)
                {
                    terrainBudget = 32;
                    contactBudget = 40;
                    Simulate(Step);
                    accumulatedTime = MathF.Max(0, accumulatedTime - Step);
                    steps++;
                }
                DrawGeometry();
                renderer.SubmitFrame(geometry.Vertices);
            }
            catch (Exception ex)
            {
                // Fail once, shut down just this cosmetic module. Never keep retrying a bad pointer path.
                // Framework Tick performs the layer/hook transition on the host
                // thread; the pose callback only stops producing geometry.
                config.BodyFluidsEnabled = false;
                ClearCore();
                log.Error(ex, "Body fluids disabled after a simulation fault.");
            }
            finally { LastCpuMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
        }
    }

    private void Simulate(float dt)
    {
        config.ClampBodyFluids();
        timedEmission = MathF.Max(0, timedEmission - dt);
        if (!surface.TryGetMouth(out var mouth)) return;
        if (Emitting)
        {
            var added = config.BodyFluidFlowMlPerSecond * 1e-6f * dt;
            reservoir += added;
            emittedVolume += added;
            var feeding = false;
            foreach (var filament in filaments)
            {
                if (!filament.Active || !filament.Fed) continue;
                feeding = true;
                var transfer = reservoir;
                filament.Volume += transfer;
                reservoir -= transfer;
                // Advected material length must grow with volume; otherwise the
                // additional fluid merely thickens an unchanged short rope.
                var addedLength = transfer * 0.3f / (MathF.PI * FedStrandRadius * FedStrandRadius);
                for (var i = 0; i < NodeCount - 1; i++) filament.Rest[i] += addedLength / (NodeCount - 1);
                break;
            }
            if (!feeding && reservoir >= FilamentSeedVolume && TryStartFilament(mouth, FilamentSeedVolume))
                reservoir -= FilamentSeedVolume;
            // Bounded reservoir; rejected overflow has an explicit accounting destination.
            if (reservoir > DropVolume * 2) { expiredVolume += reservoir - DropVolume * 2; reservoir = DropVolume * 2; }
        }
        else if (reservoir > 0 && TryAddDrop(mouth.Position, mouth.Velocity, reservoir)) reservoir = 0;

        StepFilaments(mouth, dt);
        StepDrops(dt);
        StepDeposits(dt);
        foreach (var puddle in puddles)
            if (puddle.Active)
            {
                if (puddle.Normal.Y < 0.95f && terrainBudget >= 10)
                {
                    var downhill = Gravity - Vector3.Dot(Gravity, puddle.Normal) * puddle.Normal;
                    var next = puddle.Center + downhill * (config.BodyFluidSurfaceSpeed * dt / 9.81f);
                    if (TryGround(next + Vector3.UnitY * 0.015f, next - Vector3.UnitY * 0.025f,
                        out var contact, out var normal, out _) && normal.Y > 0.1f)
                    {
                        puddle.Center = contact;
                        puddle.Normal = normal;
                        puddle.NeedsSamples = true;
                    }
                    else if (TryAddDrop(next + puddle.Normal * 0.001f, downhill * config.BodyFluidSurfaceSpeed / 9.81f, puddle.Volume))
                    { puddle.Active = false; continue; }
                }
                if (puddle.NeedsSamples) SamplePuddle(puddle);
                puddle.Age += dt;
                if (puddle.Age >= config.BodyFluidPuddleLifetime)
                { expiredVolume += puddle.Volume; puddle.Active = false; }
            }
    }

    private bool TryStartFilament(FluidSurfaceSample mouth, float volume)
    {
        // A floor directly below a fallen mouth must not start a thread already through the terrain.
        if (TryGround(mouth.Position + Vector3.UnitY * 0.001f,
            mouth.Position - Vector3.UnitY * 0.018f, out var floor, out var floorNormal, out _) && floorNormal.Y > 0.1f)
            return TryAddPuddle(floor, floorNormal, volume);
        foreach (var filament in filaments)
        {
            if (filament.Active) continue;
            filament.Active = true;
            filament.TipAttached = false;
            filament.Fed = true;
            filament.Volume = volume;
            filament.Age = 0;
            filament.DrainRate = 0;
            var initialSegment = volume * 0.3f / (MathF.PI * FedStrandRadius * FedStrandRadius * (NodeCount - 1));
            for (var i = 0; i < NodeCount; i++)
            {
                filament.Points[i] = filament.Previous[i] = mouth.Position - Vector3.UnitY * (initialSegment * i);
                filament.Velocities[i] = mouth.Velocity;
                if (i + 1 < NodeCount) filament.Rest[i] = initialSegment;
            }
            return true;
        }
        return false;
    }

    private void StepFilaments(FluidSurfaceSample mouth, float dt)
    {
        foreach (var filament in filaments)
        {
            if (!filament.Active) continue;
            if (surface.BudgetExhausted || terrainBudget < 2 || contactBudget < 2) continue;
            filament.Age += dt;
            var tip = default(FluidSurfaceSample);
            if (filament.TipAttached && !surface.TryEvaluate(filament.TipAnchor, out tip))
            {
                if (!surface.BudgetExhausted) { expiredVolume += filament.Volume; filament.Active = false; }
                continue;
            }
            if (filament.TipAttached)
            {
                // Feed a contacted surface immediately while the short bridge
                // thins, rather than holding all fluid until its safety timeout.
                var drain = MathF.Min(filament.Volume, filament.DrainRate * dt);
                if (drain > 0 && TryAddDeposit(filament.TipAnchor, drain, false)) filament.Volume -= drain;
                if (filament.Volume <= 1e-12f)
                { expiredVolume += filament.Volume; filament.Volume = 0; filament.Active = false; continue; }
                if (surface.BudgetExhausted) continue;
            }
            for (var i = 0; i < NodeCount; i++)
            {
                filament.Previous[i] = filament.Points[i];
                if (i == 0) filament.Points[i] = mouth.Position;
                else if (filament.TipAttached && i == NodeCount - 1) filament.Points[i] = tip.Position;
                else
                {
                    filament.Velocities[i] = (filament.Velocities[i] + Gravity * dt) * MathF.Exp(-0.7f * dt);
                    filament.Points[i] += filament.Velocities[i] * dt;
                }
            }
            Array.Clear(filament.Lambda);
            var alpha = 0.000002f / (dt * dt);
            // XPBD compliant stretch; rest length relaxation supplies the fluid-like extension.
            for (var iteration = 0; iteration < 4; iteration++)
                for (var i = 0; i < NodeCount - 1; i++)
                {
                    var delta = filament.Points[i + 1] - filament.Points[i];
                    var length = delta.Length();
                    if (length < 1e-6f) continue;
                    var wa = i == 0 ? 0f : 1f;
                    var wb = filament.TipAttached && i + 1 == NodeCount - 1 ? 0f : 1f;
                    var dl = (-(length - filament.Rest[i]) - alpha * filament.Lambda[i]) / (wa + wb + alpha);
                    // Liquid threads resist extension, but do not push compressed
                    // nodes apart like a spring. A tensile impulse is non-positive.
                    var nextLambda = MathF.Min(0, filament.Lambda[i] + dl);
                    dl = nextLambda - filament.Lambda[i];
                    filament.Lambda[i] = nextLambda;
                    var correction = delta * (dl / length);
                    filament.Points[i] -= wa * correction;
                    filament.Points[i + 1] += wb * correction;
                }
            var totalLength = 0f;
            for (var i = 0; i < NodeCount; i++)
            {
                filament.Velocities[i] = i == 0 ? mouth.Velocity :
                    filament.TipAttached && i == NodeCount - 1 ? tip.Velocity :
                    (filament.Points[i] - filament.Previous[i]) / dt;
                if (i + 1 < NodeCount)
                {
                    var length = Vector3.Distance(filament.Points[i], filament.Points[i + 1]);
                    totalLength += length;
                    filament.Rest[i] += MathF.Max(0, length - filament.Rest[i]) *
                        (1 - MathF.Exp(-dt / config.BodyFluidFilamentRelaxation));
                }
            }
            // Remove axial oscillation without damping inherited whole-body motion.
            var axialDecay = 1 - MathF.Exp(-8f * dt);
            for (var i = 0; i < NodeCount - 1; i++)
            {
                var delta = filament.Points[i + 1] - filament.Points[i];
                var length = delta.Length();
                if (length < 1e-6f) continue;
                var direction = delta / length;
                var wa = i == 0 ? 0f : 1f;
                var wb = filament.TipAttached && i + 1 == NodeCount - 1 ? 0f : 1f;
                var relative = Vector3.Dot(filament.Velocities[i + 1] - filament.Velocities[i], direction);
                var impulse = direction * (relative * axialDecay / (wa + wb));
                filament.Velocities[i] += wa * impulse;
                filament.Velocities[i + 1] -= wb * impulse;
            }
            var last = NodeCount - 1;
            if (!WorldGeometryBuilder.Finite(filament.Points[last]) || !WorldGeometryBuilder.Finite(filament.Velocities[last]))
            { expiredVolume += filament.Volume; filament.Active = false; continue; }
            if (!filament.TipAttached && filament.Age > Step * 2 && contactBudget > 0)
            {
                contactBudget--;
                var bodyHit = surface.TryContact(filament.Previous[last], filament.Points[last], Radius(filament.Volume) * 0.6f,
                    out var anchor, out var contact, out var bodyFraction) && Vector3.Distance(contact.Position, mouth.Position) > 0.006f;
                if (surface.BudgetExhausted)
                {
                    for (var n = 1; n < NodeCount; n++)
                    { filament.Points[n] = filament.Previous[n]; filament.Velocities[n] = Vector3.Zero; }
                    continue;
                }
                var groundHit = TryGround(filament.Previous[last], filament.Points[last], out var floor, out var floorNormal, out var groundFraction);
                if (groundHit && floorNormal.Y > 0.1f && (!bodyHit || groundFraction < bodyFraction))
                {
                    if (TryAddPuddle(floor, floorNormal, filament.Volume)) filament.Active = false;
                    else { expiredVolume += filament.Volume; filament.Active = false; }
                    continue;
                }
                if (bodyHit)
                {
                    filament.TipAttached = true;
                    filament.Fed = false;
                    filament.DrainRate = filament.Volume / Math.Clamp(config.BodyFluidFilamentRelaxation, 0.1f, 0.4f);
                    filament.TipAnchor = anchor;
                    filament.Points[last] = contact.Position;
                    filament.Velocities[last] = contact.Velocity;
                }
            }
            // Check a rotating interior node as well; deposits own the volume after contact.
            var middle = 2 + (int)(steps % (NodeCount - 4));
            if (filament.Age > 0.12f && !filament.TipAttached && contactBudget > 0)
            {
                contactBudget--;
                var bodyHit = surface.TryContact(filament.Previous[middle], filament.Points[middle], 0.0007f,
                    out var anchor, out _, out var bodyFraction);
                if (surface.BudgetExhausted)
                {
                    for (var n = 1; n < NodeCount; n++)
                    { filament.Points[n] = filament.Previous[n]; filament.Velocities[n] = Vector3.Zero; }
                    continue;
                }
                var groundHit = TryGround(filament.Previous[middle], filament.Points[middle],
                    out var floor, out var normal, out var groundFraction) && normal.Y > 0.1f;
                if (groundHit && (!bodyHit || groundFraction <= bodyFraction))
                {
                    if (TryAddPuddle(floor, normal, filament.Volume)) filament.Active = false;
                    else { expiredVolume += filament.Volume; filament.Active = false; }
                    continue;
                }
                if (bodyHit && TryAddDeposit(anchor, filament.Volume, false))
                { filament.Active = false; continue; }
            }
            else if (terrainBudget > 0 && TryGround(filament.Previous[middle], filament.Points[middle],
                out var interiorFloor, out var interiorNormal, out _) && interiorNormal.Y > 0.1f)
            {
                if (TryAddPuddle(interiorFloor, interiorNormal, filament.Volume)) filament.Active = false;
                else { expiredVolume += filament.Volume; filament.Active = false; }
                continue;
            }
            var radius = MathF.Sqrt((filament.Volume * 0.3f) / (MathF.PI * MathF.Max(0.005f, totalLength)));
            if (totalLength > config.BodyFluidFilamentLength || radius < 0.00015f ||
                filament.Age > 8f || !Emitting)
            {
                if (filament.TipAttached)
                {
                    if (TryAddDeposit(filament.TipAnchor, filament.Volume, false)) filament.Active = false;
                }
                else if (TryAddDrop(filament.Points[last], filament.Velocities[last], filament.Volume))
                    filament.Active = false;
            }
        }
    }

    private bool TryAddDrop(Vector3 position, Vector3 velocity, float volume)
    {
        for (var i = 0; i < drops.Length; i++)
            if (!drops[i].Active)
            {
                drops[i] = new Drop { Active = true, Position = position, Velocity = velocity, Volume = volume };
                return true;
            }
        return false;
    }

    private void StepDrops(float dt)
    {
        for (var slot = 0; slot < drops.Length; slot++)
        {
            var index = (slot + (int)(steps % drops.Length)) % drops.Length;
            ref var drop = ref drops[index];
            if (!drop.Active) continue;
            drop.Age += dt;
            if (drop.Age > 8f || !WorldGeometryBuilder.Finite(drop.Position) || !WorldGeometryBuilder.Finite(drop.Velocity))
            { expiredVolume += drop.Volume; drop.Active = false; continue; }
            if (contactBudget <= 0 || terrainBudget <= 0) continue;
            var velocity = drop.Velocity + Gravity * dt;
            var next = drop.Position + velocity * dt;
            contactBudget--;
            var bodyHit = surface.TryContact(drop.Position, next, Radius(drop.Volume), out var anchor, out _, out var fraction);
            if (surface.BudgetExhausted) continue;
            var groundHit = TryGround(drop.Position, next, out var point, out var normal, out var groundFraction);
            if (bodyHit && (!groundHit || fraction <= groundFraction))
            {
                if (TryAddDeposit(anchor, drop.Volume, false)) drop.Active = false;
                else drop.Velocity = Vector3.Zero; // Retain volume and retry instead of penetrating.
            }
            else if (groundHit && normal.Y > 0.1f)
            {
                if (TryAddPuddle(point, normal, drop.Volume)) drop.Active = false;
                else { expiredVolume += drop.Volume; drop.Active = false; }
            }
            else { drop.Position = next; drop.Velocity = velocity; }
        }
    }

    private bool TryAddDeposit(FluidSurfaceAnchor anchor, float volume, bool trail)
    {
        if (!surface.TryEvaluate(anchor, out var incoming)) return false;
        for (var i = 0; i < deposits.Length; i++)
        {
            ref var existing = ref deposits[i];
            if (existing.Active && existing.Trail == trail && surface.TryEvaluate(existing.Anchor, out var current) &&
                Vector3.DistanceSquared(incoming.Position, current.Position) < 0.000025f &&
                Vector3.Dot(incoming.Normal, current.Normal) > 0.8f)
            { existing.Volume += volume; existing.Age = 0; return true; }
        }
        for (var i = 0; i < deposits.Length; i++)
            if (!deposits[i].Active)
            {
                deposits[i] = new Deposit { Active = true, Anchor = anchor, Volume = volume, Trail = trail };
                return true;
            }
        return false;
    }

    private void StepDeposits(float dt)
    {
        for (var i = 0; i < deposits.Length; i++)
        {
            ref var deposit = ref deposits[i];
            if (!deposit.Active) continue;
            deposit.Age += dt;
            if (!surface.TryEvaluate(deposit.Anchor, out var current))
            {
                if (!surface.BudgetExhausted) { expiredVolume += deposit.Volume; deposit.Active = false; }
                continue;
            }
            if (deposit.Age > (deposit.Trail ? 4f : 12f))
            { expiredVolume += deposit.Volume; deposit.Active = false; continue; }
            if (deposit.Trail) continue;
            var gt = Gravity - Vector3.Dot(Gravity, current.Normal) * current.Normal;
            var damping = 9.81f / config.BodyFluidSurfaceSpeed;
            var decay = MathF.Exp(-damping * dt);
            deposit.RelativeVelocity = deposit.RelativeVelocity * decay + gt * ((1 - decay) / damping);
            var oldAnchor = deposit.Anchor;
            var displacement = deposit.RelativeVelocity * dt;
            // Downward-facing skin loses beads after a short adhesion period, rather than pinning forever.
            var peel = current.Normal.Y < -0.45f && deposit.Age > 0.22f;
            if (peel || !surface.TryWalk(ref deposit.Anchor, displacement, out _))
            {
                deposit.Anchor = oldAnchor;
                if (surface.BudgetExhausted) continue;
                if (TryAddDrop(current.Position + current.Normal * 0.002f,
                    current.Velocity + deposit.RelativeVelocity, deposit.Volume)) deposit.Active = false;
                continue;
            }
            deposit.TrailTimer += dt;
            if (deposit.TrailTimer > 0.06f && displacement.LengthSquared() > 1e-7f && deposit.Volume > DropVolume * 0.2f)
            {
                var trailVolume = deposit.Volume * 0.12f;
                if (TryAddDeposit(oldAnchor, trailVolume, true)) deposit.Volume -= trailVolume;
                deposit.TrailTimer = 0;
            }
        }
    }

    private bool TryGround(Vector3 start, Vector3 end, out Vector3 point, out Vector3 normal, out float fraction)
    {
        point = normal = default;
        fraction = 1;
        if (terrainBudget <= 0) return false;
        var delta = end - start;
        var length = delta.Length();
        if (length < 1e-6f) return false;
        terrainBudget--;
        if (!BGCollisionModule.RaycastMaterialFilter(start, delta / length, out var hit, length)) return false;
        point = new Vector3(hit.Point.X, hit.Point.Y, hit.Point.Z);
        normal = new Vector3(hit.Normal.X, hit.Normal.Y, hit.Normal.Z);
        if (!WorldGeometryBuilder.Finite(normal) || normal.LengthSquared() < 0.1f)
        {
            var a = new Vector3(hit.V1.X, hit.V1.Y, hit.V1.Z);
            var b = new Vector3(hit.V2.X, hit.V2.Y, hit.V2.Z);
            var c = new Vector3(hit.V3.X, hit.V3.Y, hit.V3.Z);
            normal = Vector3.Cross(b - a, c - a);
        }
        if (!WorldGeometryBuilder.Finite(point) || !WorldGeometryBuilder.Finite(normal) || normal.LengthSquared() < 1e-8f) return false;
        normal = Vector3.Normalize(normal);
        if (Vector3.Dot(normal, delta) > 0) normal = -normal;
        fraction = Math.Clamp(Vector3.Distance(start, point) / length, 0, 1);
        return true;
    }

    private bool TryAddPuddle(Vector3 center, Vector3 normal, float volume)
    {
        foreach (var puddle in puddles)
            if (puddle.Active && Vector3.DistanceSquared(puddle.Center, center) < 0.0009f &&
                Vector3.Dot(puddle.Normal, normal) > 0.96f)
            {
                puddle.Volume += volume;
                puddle.Age = 0;
                puddle.NeedsSamples = true;
                // Expansion is admitted only after new boundary samples validate its shape.
                SamplePuddle(puddle);
                return true;
            }
        foreach (var puddle in puddles)
            if (!puddle.Active)
            {
                puddle.Active = true;
                puddle.Center = center;
                puddle.Normal = normal;
                puddle.Volume = volume;
                puddle.Age = 0;
                puddle.Radius = 0;
                puddle.NeedsSamples = true;
                Array.Clear(puddle.BoundaryValid);
                Array.Clear(puddle.Boundary);
                SamplePuddle(puddle);
                return true;
            }
        return false;
    }

    private void SamplePuddle(Puddle puddle)
    {
        if (terrainBudget < 8) return; // Defer growth; never replace a valid shape with unsampled edges.
        var radius = Math.Clamp(MathF.Sqrt(puddle.Volume / (MathF.PI * 0.00005f)), 0.005f, 0.07f);
        var normal = puddle.Normal;
        var tangent = Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var bitangent = Vector3.Cross(normal, tangent);
        for (var i = 0; i < 8; i++)
        {
            var offset = tangent * (MathF.Cos(i * MathF.Tau / 8) * radius) + bitangent * (MathF.Sin(i * MathF.Tau / 8) * radius);
            var sample = puddle.Center + offset;
            var valid = TryGround(sample + Vector3.UnitY * 0.015f, sample - Vector3.UnitY * 0.025f,
                out var point, out var hitNormal, out _) && Vector3.Dot(normal, hitNormal) > 0.85f &&
                MathF.Abs(Vector3.Dot(point - puddle.Center, normal)) < 0.008f;
            puddle.BoundaryValid[i] = valid;
            puddle.Boundary[i] = point;
        }
        puddle.Radius = radius;
        puddle.NeedsSamples = false;
    }

    private void DrawGeometry()
    {
        geometry.Reset();
        var color = new Vector4(0.82f, 0.91f, 0.98f, config.BodyFluidOpacity);
        var visualScale = config.BodyFluidVisualScale;
        if (reservoir > 0 && surface.TryGetMouth(out var mouth)) geometry.AddEllipsoid(mouth.Position, Radius(reservoir) * visualScale, color, 1.35f);
        foreach (ref var drop in drops.AsSpan())
            if (drop.Active) geometry.AddEllipsoid(drop.Position, Radius(drop.Volume) * visualScale, color, 1.35f);
        foreach (ref var deposit in deposits.AsSpan())
            if (deposit.Active && surface.TryEvaluate(deposit.Anchor, out var sample))
            {
                var wetColor = color;
                wetColor.W *= deposit.Trail ? Math.Clamp(1 - deposit.Age / 4, 0, 1) * 0.55f : 1;
                geometry.AddSurfaceBead(sample.Position, sample.Normal, MathF.Min(0.006f, Radius(deposit.Volume) * visualScale), wetColor);
            }
        foreach (var filament in filaments)
            if (filament.Active)
            {
                var length = 0f;
                for (var i = 0; i < NodeCount - 1; i++) length += Vector3.Distance(filament.Points[i], filament.Points[i + 1]);
                var radius = Math.Clamp(MathF.Sqrt(filament.Volume * 0.3f / (MathF.PI * MathF.Max(length, 0.005f))), 0.00015f, 0.0015f);
                for (var i = 0; i < NodeCount - 1; i++) geometry.AddTube(filament.Points[i], filament.Points[i + 1], radius * visualScale, color);
                geometry.AddEllipsoid(filament.Points[NodeCount - 1], Radius(filament.Volume * 0.7f) * visualScale, color, 1.35f);
            }
        foreach (var puddle in puddles)
            if (puddle.Active)
            {
                var wetColor = color;
                wetColor.W *= Math.Clamp((config.BodyFluidPuddleLifetime - puddle.Age) / 3, 0, 1) * 0.65f;
                for (var i = 0; i < 8; i++)
                {
                    var j = (i + 1) % 8;
                    if (puddle.BoundaryValid[i] && puddle.BoundaryValid[j])
                        geometry.AddSurfaceTriangle(puddle.Center, puddle.Boundary[i], puddle.Boundary[j], puddle.Normal, wetColor);
                }
            }
    }

    private static float Radius(float volume) => MathF.Cbrt(MathF.Max(0, volume) * (3f / (4f * MathF.PI)));

    public string Describe()
    {
        lock (gate)
        {
            var d = 0; var s = 0; var f = 0; var p = 0;
            double activeVolume = reservoir;
            foreach (ref var drop in drops.AsSpan()) if (drop.Active) { d++; activeVolume += drop.Volume; }
            foreach (ref var deposit in deposits.AsSpan()) if (deposit.Active) { s++; activeVolume += deposit.Volume; }
            foreach (var filament in filaments) if (filament.Active) { f++; activeVolume += filament.Volume; }
            foreach (var puddle in puddles) if (puddle.Active) { p++; activeVolume += puddle.Volume; }
            return $"Body fluids: {(enabled ? "enabled" : "off")}, emitting={Emitting}, drops={d}, skin={s}, threads={f}, puddles={p}, " +
                $"poseFrames={poseFrames}, steps={steps}, vertices={geometry.Count}, CPU={LastCpuMilliseconds:F3} ms, " +
                $"skinVertices={surface.SkinVerticesThisFrame}, triangleTests={surface.TriangleTestsThisFrame}, budgetHit={surface.BudgetExhausted}, " +
                $"volume error={(emittedVolume - activeVolume - expiredVolume) * 1e6:F5} ml; surface={surface.Status}; render={renderer.Status}";
        }
    }

    public void Dispose()
    {
        bones.OnPosePrepared -= OnPosePrepared;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ClearCore();
        }
        renderer.Dispose();
    }
}
