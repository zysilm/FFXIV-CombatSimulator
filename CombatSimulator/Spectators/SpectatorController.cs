using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using CombatSimulator.Animation;
using CombatSimulator.Core;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Spectators;

/// <summary>
/// Owns client-only humanoid spectators. These actors deliberately never enter NpcSpawner,
/// NpcSelector, CombatEngine, or CombatCompanionManager; the only shared pieces are the native
/// humanoid bootstrap, the movement write helper, and the emote timeline player.
/// </summary>
public sealed unsafe partial class SpectatorController : IDisposable
{
    public const int MaxSpectators = 200;

    public static List<uint> CreateDefaultEmoteIds()
        => new() { 110, 7 };

    // ENpcBase uses 1 for the normal adult humanoid skeleton. Young, elder, and other
    // special body types do not consistently provide the shared player emote timelines.
    private const byte EmoteCompatibleBodyType = 1;

    // 200-248 stay reserved for dismemberment clones. Within the shared 0-199 range we also keep
    // a small free reserve so adding a crowd does not completely starve later enemy/party spawns.
    private const int CoreActorSlotReserve = 16;
    private const int MaxCreatesPerFrame = 4;
    private const int MaxRerollsPerFrame = 8;
    private const int MaxPendingFrames = 120;
    private const float PositionEpsilonSq = 0.0001f;
    private const float RotationEpsilon = 0.001f;

    private readonly NpcSelector npcSelector;
    private readonly MovementBlockHook movementBlock;
    private readonly EmoteTimelinePlayer emotePlayer;
    private readonly VNavmeshIpc vnavmesh;
    private readonly IClientState clientState;
    private readonly Func<bool> hasLivingFriendlySide;
    private readonly IPluginLog log;

    private readonly ConcurrentQueue<SpectatorSpawnRequest> spawnQueue = new();
    private readonly List<PendingSpectator> pendingSpectators = new();
    private readonly List<SpectatorActor> spectators = new();
    private readonly ConcurrentQueue<uint> rerollQueue = new();

    private IReadOnlyList<SpectatorEmote> rerollPool = Array.Empty<SpectatorEmote>();
    private HashSet<uint>? emoteCompatibleAppearanceIds;
    private HashSet<uint>? randomCrowdEligibleAppearanceIds;
    private uint nextEntityId = 0xF3000001;
    private bool disposed;

    public int LiveCount => spectators.Count;
    public int PendingCount => pendingSpectators.Count + spawnQueue.Count;
    public int TotalCount => LiveCount + PendingCount;
    public int RerollPendingCount => rerollQueue.Count;
    public string LastError { get; private set; } = string.Empty;
    public Vector3 LastFormationCenter { get; private set; }
    public float LastFormationEnvelopeRadius { get; private set; }
    public int LastFormationParticipantCount { get; private set; }
    public bool LastFormationUsesPlayerCenter { get; private set; }

    public SpectatorController(
        NpcSelector npcSelector,
        MovementBlockHook movementBlock,
        EmoteTimelinePlayer emotePlayer,
        VNavmeshIpc vnavmesh,
        IClientState clientState,
        Configuration config,
        Func<bool> hasLivingFriendlySide,
        IPluginLog log)
    {
        this.npcSelector = npcSelector;
        this.movementBlock = movementBlock;
        this.emotePlayer = emotePlayer;
        this.vnavmesh = vnavmesh;
        this.clientState = clientState;
        this.config = config;
        this.hasLivingFriendlySide = hasLivingFriendlySide;
        this.log = log;
        InitializeChatter();
    }

    public bool TryGetFormation(out SpectatorFormation formation)
    {
        formation = default;
        var localPlayer = Services.ObjectTable.LocalPlayer;
        if (!clientState.IsLoggedIn || localPlayer == null)
            return false;

        var positions = new List<(Vector3 Position, float Radius)>();
        var maxY = float.NegativeInfinity;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (!npc.IsSpawned || !npc.IsAlive || npc.BattleChara == null || npc.Address == nint.Zero)
                continue;

            var obj = (GameObject*)npc.BattleChara;
            var position = (Vector3)obj->Position;
            positions.Add((position, MathF.Max(0.5f, npc.HitboxRadius)));
            maxY = MathF.Max(maxY, position.Y);
        }

        if (positions.Count == 0)
        {
            formation = new SpectatorFormation(
                localPlayer.Position,
                0f,
                localPlayer.Position.Y,
                0,
                true);
            return true;
        }

        var center = Vector3.Zero;
        foreach (var item in positions)
            center += item.Position;
        center /= positions.Count;

        var envelope = 0f;
        foreach (var item in positions)
        {
            var dx = item.Position.X - center.X;
            var dz = item.Position.Z - center.Z;
            envelope = MathF.Max(envelope, MathF.Sqrt(dx * dx + dz * dz) + item.Radius);
        }

        formation = new SpectatorFormation(center, envelope, maxY, positions.Count, false);
        return true;
    }

    public SpectatorBatchResult QueueSingle(
        uint eNpcBaseId,
        string displayName,
        float requestedDistance,
        IReadOnlyList<uint> emoteIds)
    {
        if (!TryGetEmoteCompatibleAppearance(eNpcBaseId, out _))
            return FailBatch("The selected character is not an emote-compatible adult humanoid ENpc.");

        return QueueAppearances(
            new[] { new SpectatorAppearance(eNpcBaseId, displayName) },
            requestedCount: 1,
            requestedDistance,
            emoteIds,
            randomizeAppearances: false);
    }

    public SpectatorBatchResult QueueRandomBatch(
        IReadOnlyList<SpectatorAppearance> humanAppearances,
        IReadOnlyCollection<string> excludedNames,
        int requestedCount,
        float requestedDistance,
        IReadOnlyList<uint> emoteIds)
    {
        var unavailableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        unavailableNames.UnionWith(
            spectators.Select(spectator => NormalizeAppearanceName(spectator.DisplayName)));
        unavailableNames.UnionWith(
            pendingSpectators.Select(pending => NormalizeAppearanceName(pending.Actor.DisplayName)));
        unavailableNames.UnionWith(
            spawnQueue.ToArray().Select(request => NormalizeAppearanceName(request.DisplayName)));
        unavailableNames.UnionWith(excludedNames.Select(NormalizeAppearanceName));
        unavailableNames.Remove(string.Empty);

        var distinct = humanAppearances
            .Where(appearance =>
                appearance.ENpcBaseId != 0 &&
                !string.IsNullOrWhiteSpace(appearance.DisplayName) &&
                !unavailableNames.Contains(NormalizeAppearanceName(appearance.DisplayName)) &&
                IsRandomCrowdAppearanceEligible(appearance.ENpcBaseId))
            .GroupBy(
                appearance => NormalizeAppearanceName(appearance.DisplayName),
                StringComparer.OrdinalIgnoreCase)
            // A name such as Lyse can have many ENpcBase appearance variants. Keep one name in
            // the pool, but randomize which of its variants represents that name this batch.
            .Select(group =>
            {
                var variants = group.ToList();
                return variants[Random.Shared.Next(variants.Count)];
            })
            .ToList();
        if (distinct.Count == 0)
            return FailBatch("No unused, non-excluded, fully clothed, emote-compatible adult character names are available for random spectators.");

        return QueueAppearances(
            distinct,
            requestedCount,
            requestedDistance,
            emoteIds,
            randomizeAppearances: true);
    }

    private SpectatorBatchResult QueueAppearances(
        IReadOnlyList<SpectatorAppearance> appearances,
        int requestedCount,
        float requestedDistance,
        IReadOnlyList<uint> emoteIds,
        bool randomizeAppearances)
    {
        if (disposed)
            return FailBatch("Spectator controller is disposed.");
        if (!TryGetFormation(out var formation))
            return FailBatch("The local player is not available as a formation center.");

        var remaining = MaxSpectators - TotalCount;
        if (remaining <= 0)
            return FailBatch($"The spectator limit ({MaxSpectators}) has been reached.");

        var manager = ClientObjectManager.Instance();
        if (manager == null)
            return FailBatch("ClientObjectManager is unavailable.");
        var safeFreeSlots = Math.Max(
            0,
            ClientActorSlotAllocator.CountFree(manager) - spawnQueue.Count - CoreActorSlotReserve);
        if (safeFreeSlots == 0)
            return FailBatch("No spectator slot is available while preserving the combat actor reserve.");

        var count = Math.Clamp(requestedCount, 1, MaxSpectators);
        count = Math.Min(count, Math.Min(remaining, safeFreeSlots));
        if (randomizeAppearances)
            count = Math.Min(count, appearances.Count); // batch generation never duplicates a character name
        var distance = Math.Clamp(requestedDistance, 1f, 100f);
        var emotes = ResolveEmotes(emoteIds);
        var phase = ChooseBatchPhase(formation.Center, count);
        var batchAppearances = randomizeAppearances
            ? SampleWithoutReplacement(appearances, count)
            : Enumerable.Repeat(appearances[0], count).ToList();

        vnavmesh.RefreshStatus();
        for (var i = 0; i < count; i++)
        {
            var angle = phase + MathF.Tau * i / count;
            var randomizedClearance = distance * (0.75f + Random.Shared.NextSingle() * 0.5f);
            var radius = formation.EnvelopeRadius + randomizedClearance;
            var position = formation.Center + new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * radius;
            // The exact floor query is deferred to the throttled native-create path so a
            // 200-person click does not perform 200 collision raycasts in one UI frame.
            position.Y = formation.MaxY;
            var rotation = MathF.Atan2(formation.Center.X - position.X, formation.Center.Z - position.Z);
            var appearance = batchAppearances[i];

            spawnQueue.Enqueue(new SpectatorSpawnRequest(
                appearance.ENpcBaseId,
                appearance.DisplayName,
                position,
                rotation,
                PickRandomEmote(emotes)));
        }

        LastFormationCenter = formation.Center;
        LastFormationEnvelopeRadius = formation.EnvelopeRadius;
        LastFormationParticipantCount = formation.ParticipantCount;
        LastFormationUsesPlayerCenter = formation.UsesPlayerCenter;
        LastError = string.Empty;
        return new SpectatorBatchResult(true, count, Math.Max(0, requestedCount - count), string.Empty);
    }

    public SpectatorBatchResult QueueReroll(IReadOnlyList<uint> emoteIds)
    {
        if (disposed)
            return FailBatch("Spectator controller is disposed.");
        rerollPool = ResolveEmotes(emoteIds);
        rerollQueue.Clear();

        // Requests that have not allocated native actors can be replaced transactionally.
        var queued = spawnQueue.ToArray();
        spawnQueue.Clear();
        foreach (var request in queued)
            spawnQueue.Enqueue(request with { Emote = PickRandomEmote(rerollPool) });

        // Pending actors have not played yet, so changing their assignment is enough.
        foreach (var pending in pendingSpectators)
            pending.Actor.Emote = PickRandomEmote(rerollPool);

        // Live actors are reset/replayed gradually from the framework tick.
        foreach (var spectator in spectators)
            rerollQueue.Enqueue(spectator.EntityId);

        LastError = string.Empty;
        return new SpectatorBatchResult(true, TotalCount, 0, string.Empty);
    }

    public void Tick(float deltaTime)
    {
        if (disposed || !clientState.IsLoggedIn || Services.ObjectTable.LocalPlayer == null)
            return;

        // A missing manager during a live session is treated as temporary. Keep all managed
        // ownership records and retry on a later framework tick instead of orphaning actors.
        if (ClientObjectManager.Instance() == null)
            return;

        TickRequestedDespawns();

        for (var i = 0; i < MaxCreatesPerFrame; i++)
        {
            if (!spawnQueue.TryDequeue(out var request))
                break;

            bool created;
            bool capacityFailure;
            try
            {
                created = TryCreateSpectator(request, out capacityFailure);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Unhandled spectator creation failure for '{request.DisplayName}'.");
                SetError($"Spectator creation stopped safely: {ex.Message}");
                spawnQueue.Clear();
                break;
            }
            if (created)
                continue;

            if (capacityFailure)
            {
                var dropped = spawnQueue.Count;
                spawnQueue.Clear();
                if (dropped > 0)
                    LastError += $" {dropped} remaining request(s) were cancelled.";
                break;
            }
        }

        TickPendingSpectators();
        TickLiveSpectators();
        TickRerolls();
        TickChatter(deltaTime);
    }

    public void DespawnAll()
    {
        spawnQueue.Clear();
        rerollQueue.Clear();
        rerollPool = Array.Empty<SpectatorEmote>();

        foreach (var pending in pendingSpectators)
            pending.Actor.DespawnRequested = true;
        foreach (var spectator in spectators)
            spectator.DespawnRequested = true;

        TickRequestedDespawns();
        LastError = pendingSpectators.Any(item => item.Actor.DespawnRequested) ||
                    spectators.Any(item => item.DespawnRequested)
            ? "Some spectators could not be removed yet; cleanup will retry automatically."
            : string.Empty;
    }

    private void TickRequestedDespawns()
    {
        var hadRequestedDespawn = pendingSpectators.Any(item => item.Actor.DespawnRequested) ||
                                  spectators.Any(item => item.DespawnRequested);
        for (var i = pendingSpectators.Count - 1; i >= 0; i--)
        {
            var actor = pendingSpectators[i].Actor;
            if (actor.DespawnRequested && DestroyActor(actor, resetEmote: false))
                pendingSpectators.RemoveAt(i);
        }

        for (var i = spectators.Count - 1; i >= 0; i--)
        {
            var actor = spectators[i];
            if (actor.DespawnRequested && DestroyActor(actor, resetEmote: true))
                spectators.RemoveAt(i);
        }

        if (hadRequestedDespawn &&
            !pendingSpectators.Any(item => item.Actor.DespawnRequested) &&
            !spectators.Any(item => item.DespawnRequested) &&
            LastError.StartsWith("Some spectators could not be removed", StringComparison.Ordinal))
        {
            LastError = string.Empty;
        }
    }

    private bool TryCreateSpectator(SpectatorSpawnRequest request, out bool capacityFailure)
    {
        capacityFailure = false;
        var manager = ClientObjectManager.Instance();
        if (manager == null)
        {
            SetError("ClientObjectManager is unavailable.");
            capacityFailure = true;
            return false;
        }

        var localPlayer = Services.ObjectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address == nint.Zero)
        {
            SetError("The local player is unavailable as a humanoid clone source.");
            capacityFailure = true;
            return false;
        }

        var source = (Character*)localPlayer.Address;
        if (((byte*)&source->DrawData.CustomizeData)[0] == 0)
        {
            SetError("The local player is not currently using a humanoid model.");
            capacityFailure = true;
            return false;
        }

        if (!TryGetEmoteCompatibleAppearance(request.ENpcBaseId, out var appearance))
        {
            SetError($"Adult humanoid ENpc {request.ENpcBaseId} is no longer emote-compatible or available.");
            capacityFailure = true;
            return false;
        }

        var spawnPosition = request.Position;
        spawnPosition.Y = SnapToFloor(spawnPosition.X, request.Position.Y, spawnPosition.Z);

        if (ClientActorSlotAllocator.CountFree(manager) <= CoreActorSlotReserve)
        {
            SetError("No spectator slot is available while preserving the combat actor reserve.");
            capacityFailure = true;
            return false;
        }

        var hint = ClientActorSlotAllocator.FindFreeDescending(manager);
        if (hint == uint.MaxValue)
        {
            SetError("No free client actor slot is available (spectators share slots with enemies and companions).");
            capacityFailure = true;
            return false;
        }

        var createResult = manager->CreateBattleCharacter(hint);
        if (createResult == 0xFFFFFFFF)
        {
            SetError("CreateBattleCharacter failed because no client actor slot is available.");
            capacityFailure = true;
            return false;
        }

        var index = (int)createResult;
        var address = nint.Zero;
        SpectatorActor? movementOwner = null;
        var committed = false;
        try
        {
            // Never consume the 200-248 range reserved by the dismemberment system.
            if (index < 0 || index >= ClientActorSlotAllocator.SharedSlotLimit)
            {
                SetError("The safe spectator client actor range is full.");
                capacityFailure = true;
                return false;
            }

            var obj = manager->GetObjectByIndex((ushort)index);
            if (obj == null)
            {
                SetError($"Client actor slot {index} returned no object after creation.");
                capacityFailure = true;
                return false;
            }

            address = (nint)obj;
            var battleChara = (BattleChara*)obj;
            var character = (Character*)obj;

            obj->ObjectKind = ObjectKind.Pc;
            obj->SubKind = 0;
            obj->TargetableStatus = 0;
            obj->RenderFlags = VisibilityFlags.None;
            obj->Position = spawnPosition;
            obj->Rotation = request.Rotation;
            WriteName((GameObject*)obj, request.DisplayName);

            const CharacterSetupContainer.CopyFlags flags =
                CharacterSetupContainer.CopyFlags.ClassJob |
                CharacterSetupContainer.CopyFlags.WeaponHiding;
            character->CharacterSetup.CopyFromCharacter(source, flags);
            OverwriteAppearance(character, appearance);
            character->CharacterSetup.CopyFromCharacter(character, CharacterSetupContainer.CopyFlags.None);

            // CopyFromCharacter also copies base GameObject fields. Spectators remain synthetic,
            // untargetable PCs at their assigned point, never player clones in gameplay state.
            obj->ObjectKind = ObjectKind.Pc;
            obj->SubKind = 0;
            obj->TargetableStatus = 0;
            obj->RenderFlags = VisibilityFlags.None;
            obj->Position = spawnPosition;
            obj->Rotation = request.Rotation;
            WriteName((GameObject*)obj, request.DisplayName);
            character->SetMode(CharacterModes.Normal, 0);

            var entityId = nextEntityId++;
            obj->EntityId = entityId;

            var actor = new SpectatorActor
            {
                ObjectIndex = index,
                EntityId = entityId,
                Address = address,
                DisplayName = request.DisplayName,
                ENpcBaseId = request.ENpcBaseId,
                FixedPosition = spawnPosition,
                FixedRotation = request.Rotation,
                MainHandWeapon = appearance.ModelMainHand,
                OffHandWeapon = appearance.ModelOffHand,
                Emote = request.Emote,
            };

            movementOwner = actor;
            movementBlock.AddApproachNpc(address, actor);
            pendingSpectators.Add(new PendingSpectator(actor));
            committed = true;
            LastError = string.Empty;
            log.Verbose($"Spectator '{request.DisplayName}' created at slot {index}, entity=0x{entityId:X}; pending draw.");
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed to create spectator '{request.DisplayName}'.");
            SetError($"Failed to create spectator '{request.DisplayName}': {ex.Message}");
            capacityFailure = true;
            return false;
        }
        finally
        {
            if (!committed)
            {
                if (address != nint.Zero && movementOwner != null)
                    movementBlock.RemoveApproachNpc(address, movementOwner);
                TryDeleteFreshObject(manager, index, address);
            }
        }
    }

    private void TickPendingSpectators()
    {
        for (var i = pendingSpectators.Count - 1; i >= 0; i--)
        {
            var pending = pendingSpectators[i];
            if (pending.Actor.DespawnRequested)
                continue;
            pending.FramesWaited++;

            if (!TryGetOwnedObject(pending.Actor, out var battleChara, out var obj))
            {
                movementBlock.RemoveApproachNpc(pending.Actor.Address, pending.Actor);
                pendingSpectators.RemoveAt(i);
                SetError($"Pending spectator '{pending.Actor.DisplayName}' lost its client actor slot.");
                continue;
            }

            movementBlock.AddApproachNpc(pending.Actor.Address, pending.Actor);

            try
            {
                if (!battleChara->IsReadyToDraw())
                {
                    if (pending.FramesWaited < MaxPendingFrames)
                        continue;

                    pending.Actor.DespawnRequested = true;
                    if (DestroyActor(pending.Actor, resetEmote: false))
                        pendingSpectators.RemoveAt(i);
                    SetError($"Spectator '{pending.Actor.DisplayName}' timed out while preparing its model and was scheduled for safe removal.");
                    continue;
                }

                battleChara->EnableDraw();
                pending.Actor.IsDrawn = true;
                LoadWeapons((Character*)battleChara, pending.Actor);
                obj->ObjectKind = ObjectKind.Pc;
                obj->SubKind = 0;
                obj->TargetableStatus = 0;
                obj->EntityId = pending.Actor.EntityId;
                movementBlock.SetApproachPosition(
                    obj,
                    pending.Actor.FixedPosition.X,
                    pending.Actor.FixedPosition.Y,
                    pending.Actor.FixedPosition.Z);
                movementBlock.SetApproachRotation(obj, pending.Actor.FixedRotation);

                PlayAssignedEmote(pending.Actor, (Character*)battleChara);
                spectators.Add(pending.Actor);
                pendingSpectators.RemoveAt(i);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Failed to finish spectator '{pending.Actor.DisplayName}'.");
                pending.Actor.DespawnRequested = true;
                if (DestroyActor(pending.Actor, resetEmote: false))
                    pendingSpectators.RemoveAt(i);
                SetError($"Failed to draw spectator '{pending.Actor.DisplayName}': {ex.Message}");
            }
        }
    }

    private void TickLiveSpectators()
    {
        for (var i = spectators.Count - 1; i >= 0; i--)
        {
            var spectator = spectators[i];
            if (spectator.DespawnRequested)
                continue;
            try
            {
                if (!TryGetOwnedObject(spectator, out _, out var obj))
                {
                    movementBlock.RemoveApproachNpc(spectator.Address, spectator);
                    spectators.RemoveAt(i);
                    continue;
                }

                // Defense in depth: even if another client system touches these fields, spectators
                // never become selectable and never enter the real player/enemy identity ranges.
                movementBlock.AddApproachNpc(spectator.Address, spectator);
                obj->ObjectKind = ObjectKind.Pc;
                obj->SubKind = 0;
                obj->TargetableStatus = 0;
                obj->EntityId = spectator.EntityId;

                var current = (Vector3)obj->Position;
                if (Vector3.DistanceSquared(current, spectator.FixedPosition) > PositionEpsilonSq)
                {
                    movementBlock.SetApproachPosition(
                        obj,
                        spectator.FixedPosition.X,
                        spectator.FixedPosition.Y,
                        spectator.FixedPosition.Z);
                }

                var rotationDelta = MathF.Atan2(
                    MathF.Sin(obj->Rotation - spectator.FixedRotation),
                    MathF.Cos(obj->Rotation - spectator.FixedRotation));
                if (MathF.Abs(rotationDelta) > RotationEpsilon)
                    movementBlock.SetApproachRotation(obj, spectator.FixedRotation);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Spectator '{spectator.DisplayName}' failed its stationary update.");
                spectator.DespawnRequested = true;
                if (DestroyActor(spectator, resetEmote: true))
                    spectators.RemoveAt(i);
                SetError($"Spectator '{spectator.DisplayName}' was scheduled for safe removal after an update failure: {ex.Message}");
            }
        }
    }

    private void TickRerolls()
    {
        for (var i = 0; i < MaxRerollsPerFrame; i++)
        {
            if (!rerollQueue.TryDequeue(out var entityId))
                break;
            var spectator = spectators.FirstOrDefault(actor => actor.EntityId == entityId);
            if (spectator == null || !TryGetOwnedObject(spectator, out var battleChara, out _))
                continue;

            try
            {
                var character = (Character*)battleChara;
                emotePlayer.ResetEmote(character);
                spectator.Emote = PickRandomEmote(rerollPool);
                PlayAssignedEmote(spectator, character);
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"Failed to re-roll spectator '{spectator.DisplayName}'.");
                SetError($"Failed to re-roll spectator '{spectator.DisplayName}': {ex.Message}");
            }
        }
    }

    private void PlayAssignedEmote(SpectatorActor actor, Character* character)
    {
        if (actor.Emote.Id == 0 || (actor.Emote.LoopTimeline == 0 && actor.Emote.IntroTimeline == 0))
            return;

        emotePlayer.PlayLoopedEmote(character, actor.Emote.LoopTimeline, actor.Emote.IntroTimeline);
    }

    private bool DestroyActor(SpectatorActor actor, bool resetEmote)
    {
        // On logout the game frees actors before managed cleanup runs. Native access is unsafe
        // there and cannot be made safe with a catch, so managed bookkeeping is all we do.
        if (!clientState.IsLoggedIn || Services.ObjectTable.LocalPlayer == null)
        {
            movementBlock.RemoveApproachNpc(actor.Address, actor);
            return true;
        }

        var manager = ClientObjectManager.Instance();
        if (manager == null)
            return false;

        try
        {
            if (!TryGetOwnedObject(actor, out var battleChara, out var obj))
            {
                // The slot is empty or has been reused by another owner. Never delete the
                // replacement, but our own bookkeeping and movement token are now obsolete.
                movementBlock.RemoveApproachNpc(actor.Address, actor);
                return true;
            }

            if (resetEmote && actor.IsDrawn)
                emotePlayer.ResetEmote((Character*)battleChara);
            CloseChatter(actor, (Character*)battleChara);
            if (actor.IsDrawn)
            {
                obj->DisableDraw();
                actor.IsDrawn = false;
            }

            manager->DeleteObjectByIndex((ushort)actor.ObjectIndex, 0);
            movementBlock.RemoveApproachNpc(actor.Address, actor);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to despawn spectator '{actor.DisplayName}'.");
            return false;
        }
    }

    private bool TryGetOwnedObject(SpectatorActor actor, out BattleChara* battleChara, out GameObject* obj)
    {
        battleChara = null;
        obj = null;
        if (!clientState.IsLoggedIn || actor.Address == nint.Zero || actor.ObjectIndex < 0 || Services.ObjectTable.LocalPlayer == null)
            return false;

        var manager = ClientObjectManager.Instance();
        if (manager == null)
            return false;

        obj = (GameObject*)manager->GetObjectByIndex((ushort)actor.ObjectIndex);
        if (obj == null || (nint)obj != actor.Address || obj->EntityId != actor.EntityId)
        {
            obj = null;
            return false;
        }

        battleChara = (BattleChara*)obj;
        return true;
    }

    private float ChooseBatchPhase(Vector3 center, int count)
    {
        var existingPositions = spectators.Select(actor => actor.FixedPosition)
            .Concat(pendingSpectators.Select(pending => pending.Actor.FixedPosition))
            .Concat(spawnQueue.ToArray().Select(request => request.Position))
            .ToList();
        if (existingPositions.Count == 0)
            return Random.Shared.NextSingle() * MathF.Tau;

        // A regular count-gon repeats after one angular slot, so search that interval for the
        // phase whose closest new/existing pair has the largest angular separation. Existing
        // spectators never move; repeated batches simply settle into the least crowded gaps.
        var period = MathF.Tau / count;
        var seed = Random.Shared.NextSingle() * period;
        const int candidateCount = 48;
        var bestPhase = seed;
        var bestSeparation = -1f;

        for (var candidate = 0; candidate < candidateCount; candidate++)
        {
            var phase = seed + period * candidate / candidateCount;
            var minimumSeparation = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var newAngle = phase + MathF.Tau * i / count;
                foreach (var position in existingPositions)
                {
                    var existingAngle = MathF.Atan2(position.X - center.X, position.Z - center.Z);
                    var delta = MathF.Abs(MathF.Atan2(
                        MathF.Sin(newAngle - existingAngle),
                        MathF.Cos(newAngle - existingAngle)));
                    minimumSeparation = MathF.Min(minimumSeparation, delta);
                }
            }

            if (minimumSeparation <= bestSeparation)
                continue;
            bestSeparation = minimumSeparation;
            bestPhase = phase;
        }

        return bestPhase;
    }

    private static List<SpectatorAppearance> SampleWithoutReplacement(
        IReadOnlyList<SpectatorAppearance> source,
        int count)
    {
        var pool = source.ToList();
        for (var i = 0; i < count; i++)
        {
            var selected = Random.Shared.Next(i, pool.Count);
            (pool[i], pool[selected]) = (pool[selected], pool[i]);
        }
        return pool.GetRange(0, count);
    }

    private static string NormalizeAppearanceName(string? displayName)
        => displayName?.Trim() ?? string.Empty;

    private void TryDeleteFreshObject(ClientObjectManager* manager, int index, nint expectedAddress)
    {
        if (!clientState.IsLoggedIn || Services.ObjectTable.LocalPlayer == null ||
            index < 0 || index >= ClientActorSlotAllocator.TotalSlotCount)
            return;

        try
        {
            var current = manager->GetObjectByIndex((ushort)index);
            // A successful CreateBattleCharacter return proves this index is our allocation.
            // When an address was captured, additionally refuse to touch a replacement owner.
            if (expectedAddress != nint.Zero && (current == null || (nint)current != expectedAddress))
                return;
            manager->DeleteObjectByIndex((ushort)index, 0);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to roll back fresh spectator slot {index}.");
        }
    }

    private bool TryGetHumanAppearance(uint eNpcBaseId, out ENpcBase appearance)
    {
        appearance = default;
        var sheet = Services.DataManager.GetExcelSheet<ENpcBase>();
        var row = sheet?.GetRowOrDefault(eNpcBaseId);
        if (row == null)
            return false;

        appearance = row.Value;
        return appearance.ModelChara.RowId == 0 && appearance.Race.RowId > 0;
    }

    public bool IsEmoteCompatibleAppearance(uint eNpcBaseId)
    {
        EnsureAppearanceCompatibilityCaches();
        return emoteCompatibleAppearanceIds!.Contains(eNpcBaseId);
    }

    private bool IsRandomCrowdAppearanceEligible(uint eNpcBaseId)
    {
        EnsureAppearanceCompatibilityCaches();
        return randomCrowdEligibleAppearanceIds!.Contains(eNpcBaseId);
    }

    private void EnsureAppearanceCompatibilityCaches()
    {
        if (emoteCompatibleAppearanceIds != null && randomCrowdEligibleAppearanceIds != null)
            return;

        emoteCompatibleAppearanceIds = new HashSet<uint>();
        randomCrowdEligibleAppearanceIds = new HashSet<uint>();
        var sheet = Services.DataManager.GetExcelSheet<ENpcBase>();
        if (sheet == null)
            return;

        foreach (var appearance in sheet)
        {
            if (appearance.ModelChara.RowId != 0 ||
                appearance.Race.RowId == 0 ||
                appearance.BodyType != EmoteCompatibleBodyType)
            {
                continue;
            }

            emoteCompatibleAppearanceIds.Add(appearance.RowId);

            // Random crowds should never choose a bare-top or bare-legs appearance. This
            // restriction deliberately does not participate in QueueSingle/Spawn Selected.
            if ((ulong)appearance.ModelBody != 0 && (ulong)appearance.ModelLegs != 0)
                randomCrowdEligibleAppearanceIds.Add(appearance.RowId);
        }
    }

    private bool TryGetEmoteCompatibleAppearance(uint eNpcBaseId, out ENpcBase appearance)
    {
        appearance = default;
        return IsEmoteCompatibleAppearance(eNpcBaseId) &&
               TryGetHumanAppearance(eNpcBaseId, out appearance) &&
               appearance.BodyType == EmoteCompatibleBodyType;
    }

    private IReadOnlyList<SpectatorEmote> ResolveEmotes(IReadOnlyList<uint> emoteIds)
    {
        if (emoteIds.Count == 0)
            return Array.Empty<SpectatorEmote>();

        var result = new List<SpectatorEmote>();
        var seen = new HashSet<uint>();
        var sheet = Services.DataManager.GetExcelSheet<Emote>();
        if (sheet == null)
            return result;

        foreach (var id in emoteIds)
        {
            if (id == 0 || !seen.Add(id))
                continue;
            var row = sheet.GetRowOrDefault(id);
            if (row == null)
                continue;

            var loop = (ushort)row.Value.ActionTimeline[0].RowId;
            var intro = (ushort)row.Value.ActionTimeline[1].RowId;
            if (loop == 0 && intro == 0)
                continue;
            result.Add(new SpectatorEmote(id, loop, intro));
        }

        return result;
    }

    private static SpectatorEmote PickRandomEmote(IReadOnlyList<SpectatorEmote> emotes)
        => emotes.Count == 0 ? default : emotes[Random.Shared.Next(emotes.Count)];

    private float SnapToFloor(float x, float referenceY, float z)
    {
        try
        {
            if (BGCollisionModule.RaycastMaterialFilter(
                    new Vector3(x, referenceY + 20f, z),
                    new Vector3(0f, -1f, 0f),
                    out var hit,
                    120f))
                return hit.Point.Y;
        }
        catch (Exception ex)
        {
            log.Verbose($"Spectator terrain floor snap failed: {ex.Message}");
        }

        if (vnavmesh.CanPathfind)
        {
            try
            {
                var point = new Vector3(x, referenceY + 10f, z);
                var floor = vnavmesh.PointOnFloor(point, false, 5f)
                            ?? vnavmesh.NearestPointReachable(point, 5f, 20f);
                if (floor.HasValue)
                    return floor.Value.Y;
            }
            catch (Exception ex)
            {
                log.Verbose($"Spectator floor snap via vnavmesh failed: {ex.Message}");
            }
        }

        return referenceY;
    }

    private static void WriteName(GameObject* obj, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(name) ? "Spectator" : name);
        for (var i = 0; i < 64; i++)
            obj->Name[i] = i < bytes.Length && i < 63 ? bytes[i] : (byte)0;
    }

    private static void OverwriteAppearance(Character* character, ENpcBase appearance)
    {
        var customize = (byte*)&character->DrawData.CustomizeData;
        customize[0x00] = (byte)appearance.Race.RowId;
        customize[0x01] = appearance.Gender;
        customize[0x02] = appearance.BodyType;
        customize[0x03] = appearance.Height;
        customize[0x04] = (byte)appearance.Tribe.RowId;
        customize[0x05] = appearance.Face;
        customize[0x06] = appearance.HairStyle;
        customize[0x07] = appearance.HairHighlight;
        customize[0x08] = appearance.SkinColor;
        customize[0x09] = appearance.EyeHeterochromia;
        customize[0x0A] = appearance.HairColor;
        customize[0x0B] = appearance.HairHighlightColor;
        customize[0x0C] = appearance.FacialFeature;
        customize[0x0D] = appearance.FacialFeatureColor;
        customize[0x0E] = appearance.Eyebrows;
        customize[0x0F] = appearance.EyeColor;
        customize[0x10] = appearance.EyeShape;
        customize[0x11] = appearance.Nose;
        customize[0x12] = appearance.Jaw;
        customize[0x13] = appearance.Mouth;
        customize[0x14] = appearance.LipColor;
        customize[0x15] = appearance.BustOrTone1;
        customize[0x16] = appearance.ExtraFeature1;
        customize[0x17] = appearance.ExtraFeature2OrBust;
        customize[0x18] = appearance.FacePaint;
        customize[0x19] = appearance.FacePaintColor;

        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Head).Value = (ulong)appearance.ModelHead;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Body).Value = (ulong)appearance.ModelBody;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Hands).Value = (ulong)appearance.ModelHands;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Legs).Value = (ulong)appearance.ModelLegs;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Feet).Value = (ulong)appearance.ModelFeet;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Ears).Value = (ulong)appearance.ModelEars;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Neck).Value = (ulong)appearance.ModelNeck;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Wrists).Value = (ulong)appearance.ModelWrists;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.RFinger).Value = (ulong)appearance.ModelRightRing;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.LFinger).Value = (ulong)appearance.ModelLeftRing;
    }

    private void LoadWeapons(Character* character, SpectatorActor actor)
    {
        if (actor.MainHandWeapon == 0 && actor.OffHandWeapon == 0)
            return;

        try
        {
            var main = actor.MainHandWeapon;
            var off = actor.OffHandWeapon;
            var mainId = *(WeaponModelId*)&main;
            var offId = *(WeaponModelId*)&off;
            character->DrawData.LoadWeapon(DrawDataContainer.WeaponSlot.MainHand, mainId, 0, 0, 0, 0, false);
            character->DrawData.LoadWeapon(DrawDataContainer.WeaponSlot.OffHand, offId, 0, 0, 0, 0, false);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to load weapons for spectator '{actor.DisplayName}'.");
        }
    }

    private SpectatorBatchResult FailBatch(string error)
    {
        SetError(error);
        return new SpectatorBatchResult(false, 0, 0, error);
    }

    private void SetError(string error)
    {
        LastError = error;
        log.Warning($"Spectators: {error}");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        DespawnAll();
        DisposeChatter();
        disposed = true;
    }

    public readonly record struct SpectatorFormation(
        Vector3 Center,
        float EnvelopeRadius,
        float MaxY,
        int ParticipantCount,
        bool UsesPlayerCenter);

    public readonly record struct SpectatorAppearance(uint ENpcBaseId, string DisplayName);

    public readonly record struct SpectatorBatchResult(
        bool Success,
        int Queued,
        int Dropped,
        string Error);

    private readonly record struct SpectatorEmote(uint Id, ushort LoopTimeline, ushort IntroTimeline);

    private sealed record SpectatorSpawnRequest(
        uint ENpcBaseId,
        string DisplayName,
        Vector3 Position,
        float Rotation,
        SpectatorEmote Emote);

    private sealed class SpectatorActor
    {
        public int ObjectIndex { get; init; }
        public uint EntityId { get; init; }
        public nint Address { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public uint ENpcBaseId { get; init; }
        public Vector3 FixedPosition { get; init; }
        public float FixedRotation { get; init; }
        public ulong MainHandWeapon { get; init; }
        public ulong OffHandWeapon { get; init; }
        public SpectatorEmote Emote { get; set; }
        public bool IsDrawn { get; set; }
        public bool DespawnRequested { get; set; }
        public double ChatterUntil { get; set; }
        public double NextChatterEligibleAt { get; set; }
        public string LastChatterLine { get; set; } = string.Empty;
    }

    private sealed class PendingSpectator
    {
        public PendingSpectator(SpectatorActor actor) => Actor = actor;
        public SpectatorActor Actor { get; }
        public int FramesWaited { get; set; }
    }
}
