using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using CombatSimulator.Integration;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using BepuSimulation = BepuPhysics.Simulation;

namespace CombatSimulator.Animation;

/// <summary>
/// Dismemberment "substitute" half: for each severed limb, spawn a CLONE of the dying character that
/// shows ONLY that limb (everything else collapsed to ~0 scale), copies the full appearance
/// (CharacterSetup base + live Glamourer state when installed), freezes its animation, and drives it as
/// a rigid body (reusing the weapon-drop pattern) so the visible limb tumbles to the ground. The body
/// side hides the same limb separately (RagdollController.HideLimbSubtree), so it looks severed.
/// </summary>
public unsafe class DismembermentController : IDisposable
{
    private readonly BoneTransformService boneService;
    private readonly GlamourerIpc glamourerIpc;
    private readonly IObjectTable objectTable;
    private readonly Configuration config;
    private readonly IPluginLog log;

    // Limb physics proxy (one capsule shape shared by all limb bodies). Generic limb-sized chunk.
    private const float LimbRadius = 0.06f;
    private const float LimbHalfLength = 0.14f;
    private const float LimbMass = 4f;
    private const int MaxPendingFrames = 120;
    private const float CloneTtl = 25f;

    private BufferPool? bufferPool;
    private BepuSimulation? simulation;
    private TypedIndex limbShapeIndex;
    private BodyInertia limbInertia;
    private uint nextEntityId = 0xF2000001;

    private sealed class Clone
    {
        public nint SourceAddress;
        public string LimbRootBone = "";
        public int ObjectIndex = -1;
        public BattleChara* Chara;
        public IGameObject? GameObjectRef;
        public Vector3 SeveranceWorldPos;
        public Quaternion SeveranceWorldRot;
        public bool DrawEnabled;
        public int FramesWaited;
        public int SettleFrames;   // let the clone idle into a real pose before freezing
        public bool Armed;         // frozen + body created + driving
        public int LimbIndex = -1;
        public Vector3 LimbRootModelPos;
        public List<(int Idx, Vector3 T, Quaternion R, Vector3 S)>? LimbSnapshot; // frozen limb pose
        public BodyHandle? Body;
        public StaticHandle? GroundTile;
        public TypedIndex? GroundShape;
        public string? GlamourBase64;
        public int GlamourFramesUntil = -1;
        public int GlamourAttemptsLeft;
        public float Ttl = CloneTtl;
    }

    private sealed class Pending
    {
        public nint SourceAddress;
        public string LimbRootBone = "";
        public float Delay;
        public string? GlamourBase64;
    }

    private readonly List<Clone> clones = new();
    private readonly List<Pending> pending = new();
    private readonly HashSet<int> allocatedIndices = new();

    public DismembermentController(BoneTransformService boneService, GlamourerIpc glamourerIpc,
        IObjectTable objectTable, Configuration config, IPluginLog log)
    {
        this.boneService = boneService;
        this.glamourerIpc = glamourerIpc;
        this.objectTable = objectTable;
        this.config = config;
        this.log = log;
        boneService.OnRenderFrame += OnRenderFrame;
    }

    public bool HasAny => clones.Count > 0 || pending.Count > 0;

    /// <summary>Schedule a severed-limb clone for <paramref name="limbRootBone"/> on the given character,
    /// firing after <paramref name="delay"/> (matches the ragdoll activation delay).</summary>
    public void SpawnFor(nint sourceAddress, string limbRootBone, float delay, string? glamourBase64)
    {
        if (sourceAddress == nint.Zero || string.IsNullOrEmpty(limbRootBone)) return;
        if (clones.Exists(c => c.SourceAddress == sourceAddress && c.LimbRootBone == limbRootBone)) return;
        if (pending.Exists(p => p.SourceAddress == sourceAddress && p.LimbRootBone == limbRootBone)) return;
        pending.Add(new Pending
        {
            SourceAddress = sourceAddress,
            LimbRootBone = limbRootBone,
            Delay = MathF.Max(0f, delay),
            GlamourBase64 = glamourBase64,
        });
    }

    public void RemoveFor(nint sourceAddress)
    {
        pending.RemoveAll(p => p.SourceAddress == sourceAddress);
        for (int i = clones.Count - 1; i >= 0; i--)
            if (clones[i].SourceAddress == sourceAddress)
            {
                DespawnClone(clones[i]);
                clones.RemoveAt(i);
            }
    }

    public void RemoveAll()
    {
        pending.Clear();
        if (clones.Count == 0) return;
        foreach (var c in clones) DespawnClone(c);
        clones.Clear();
    }

    private void OnRenderFrame()
    {
        try
        {
            // Tick pending spawns (delay countdown).
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                p.Delay -= 1f / 60f;
                if (p.Delay <= 0f)
                {
                    pending.RemoveAt(i);
                    TrySpawn(p);
                }
            }

            if (clones.Count == 0) return;

            // Draw-ready poll.
            foreach (var c in clones)
                if (!c.DrawEnabled) TryEnableDraw(c);

            if (simulation != null) simulation.Timestep(1f / 60f);

            for (int i = clones.Count - 1; i >= 0; i--)
            {
                var c = clones[i];
                c.Ttl -= 1f / 60f;
                if (c.Ttl <= 0f)
                {
                    DespawnClone(c);
                    clones.RemoveAt(i);
                    continue;
                }
                if (!c.DrawEnabled) continue;
                ApplyDeferredGlamour(c);
                UpdateClone(c);
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "DismembermentController: error in render frame");
        }
    }

    private void TrySpawn(Pending p)
    {
        // Capture the severance world pose from the (dying) source skeleton.
        var skelN = boneService.TryGetSkeleton(p.SourceAddress);
        if (skelN == null) { log.Warning($"Dismember: source skeleton unavailable 0x{p.SourceAddress:X}"); return; }
        var skel = skelN.Value;
        var limbIdx = boneService.ResolveBoneIndex(skel, p.LimbRootBone);
        if (limbIdx < 0) { log.Warning($"Dismember: bone '{p.LimbRootBone}' not found"); return; }

        var srcSk = skel.CharBase->Skeleton;
        if (srcSk == null) return;
        var srcPos = new Vector3(srcSk->Transform.Position.X, srcSk->Transform.Position.Y, srcSk->Transform.Position.Z);
        var srcRot = new Quaternion(srcSk->Transform.Rotation.X, srcSk->Transform.Rotation.Y, srcSk->Transform.Rotation.Z, srcSk->Transform.Rotation.W);
        ref var lm = ref skel.Pose->ModelPose.Data[limbIdx];
        var limbModelPos = new Vector3(lm.Translation.X, lm.Translation.Y, lm.Translation.Z);
        var limbModelRot = new Quaternion(lm.Rotation.X, lm.Rotation.Y, lm.Rotation.Z, lm.Rotation.W);
        var severancePos = srcPos + Vector3.Transform(limbModelPos, srcRot);
        var severanceRot = Quaternion.Normalize(srcRot * limbModelRot);

        var src = (Character*)p.SourceAddress;
        if (((byte*)&src->DrawData.CustomizeData)[0] == 0) { log.Warning("Dismember: source not humanoid"); return; }

        var clientObjMgr = ClientObjectManager.Instance();
        if (clientObjMgr == null) return;
        var hint = FindFreeObjectHint();
        var createResult = clientObjMgr->CreateBattleCharacter(hint);
        if (createResult == 0xFFFFFFFF) { log.Warning("Dismember: CreateBattleCharacter failed (no slot)"); return; }

        var index = (int)createResult;
        allocatedIndices.Add(index);
        var obj = clientObjMgr->GetObjectByIndex((ushort)index);
        if (obj == null) { allocatedIndices.Remove(index); log.Warning("Dismember: object null after create"); return; }

        var chara = (BattleChara*)obj;
        var character = (Character*)chara;

        obj->ObjectKind = ObjectKind.Pc;
        obj->SubKind = 0;
        obj->TargetableStatus = 0; // never targetable
        obj->RenderFlags = (VisibilityFlags)0;
        obj->Position = severancePos;
        obj->Rotation = 0f;
        for (int j = 0; j < 64; j++) obj->Name[j] = 0; // no nameplate

        const CharacterSetupContainer.CopyFlags flags =
            CharacterSetupContainer.CopyFlags.ClassJob | CharacterSetupContainer.CopyFlags.WeaponHiding;
        character->CharacterSetup.CopyFromCharacter(src, flags);
        character->CharacterSetup.CopyFromCharacter(character, CharacterSetupContainer.CopyFlags.None);
        character->SetMode(CharacterModes.Normal, 0);

        IGameObject? gameObjectRef = null;
        try { gameObjectRef = objectTable.CreateObjectReference((nint)obj); }
        catch (Exception ex) { log.Warning(ex, "Dismember: CreateObjectReference failed"); }

        obj->EntityId = nextEntityId++;

        clones.Add(new Clone
        {
            SourceAddress = p.SourceAddress,
            LimbRootBone = p.LimbRootBone,
            ObjectIndex = index,
            Chara = chara,
            GameObjectRef = gameObjectRef,
            SeveranceWorldPos = severancePos,
            SeveranceWorldRot = severanceRot,
            GlamourBase64 = p.GlamourBase64,
        });
        log.Info($"Dismember: clone idx={index} bone={p.LimbRootBone} at ({severancePos.X:F1},{severancePos.Y:F1},{severancePos.Z:F1})");
    }

    private void TryEnableDraw(Clone c)
    {
        c.FramesWaited++;
        if (c.Chara == null) return;
        if (!c.Chara->IsReadyToDraw() && c.FramesWaited < MaxPendingFrames) return;

        c.Chara->EnableDraw();
        c.DrawEnabled = true;
        c.SettleFrames = 8;        // let it idle into a real pose before freezing
        c.GlamourFramesUntil = 3;  // apply WHILE the clone is still alive (frozen actors may not redraw)
        c.GlamourAttemptsLeft = 6;
        log.Info($"Dismember: clone idx={c.ObjectIndex} drawn (settling)");
    }

    private void UpdateClone(Clone c)
    {
        if (c.Chara == null) return;
        var drawObj = ((GameObject*)c.Chara)->DrawObject;
        if (drawObj == null) return;

        var skelN = boneService.TryGetSkeleton((nint)c.Chara);
        if (skelN == null) return;
        var skel = skelN.Value;

        if (c.LimbIndex < 0)
            c.LimbIndex = boneService.ResolveBoneIndex(skel, c.LimbRootBone);
        if (c.LimbIndex < 0) return; // nothing we can do until the limb resolves

        // Each frame: show ONLY the limb (others thrown far away) and hide the clone's weapons.
        HideAllButLimb(skel, c.LimbIndex);
        HideWeapons(c);

        if (!c.Armed)
        {
            // Let the pose settle (the limb gets a real shape), then freeze + spawn the body.
            if (--c.SettleFrames > 0) return;
            ref var lm = ref skel.Pose->ModelPose.Data[c.LimbIndex];
            c.LimbRootModelPos = new Vector3(lm.Translation.X, lm.Translation.Y, lm.Translation.Z);
            ((Character*)c.Chara)->Timeline.OverallSpeed = 0f;
            // Snapshot the settled limb pose so we can re-assert it every frame — guarantees the limb
            // is a frozen rigid chunk and can't keep breathing.
            c.LimbSnapshot = new List<(int, Vector3, Quaternion, Vector3)>();
            var nn = Math.Min(skel.BoneCount, skel.ParentCount);
            for (int i = 0; i < nn; i++)
            {
                if (!IsDescendantOrSelf(skel, i, c.LimbIndex)) continue;
                ref var bm = ref skel.Pose->ModelPose.Data[i];
                c.LimbSnapshot.Add((i,
                    new Vector3(bm.Translation.X, bm.Translation.Y, bm.Translation.Z),
                    new Quaternion(bm.Rotation.X, bm.Rotation.Y, bm.Rotation.Z, bm.Rotation.W),
                    new Vector3(bm.Scale.X, bm.Scale.Y, bm.Scale.Z)));
            }
            EnsureSimulation();
            if (simulation != null)
            {
                c.Body = simulation.Bodies.Add(BodyDescription.CreateDynamic(
                    new RigidPose(c.SeveranceWorldPos, c.SeveranceWorldRot),
                    default(BodyVelocity),
                    limbInertia,
                    new CollidableDescription(limbShapeIndex, 0.04f),
                    new BodyActivityDescription(0.01f)));
                (c.GroundTile, c.GroundShape) = CreateTerrainPatch(c.SeveranceWorldPos.X, c.SeveranceWorldPos.Z, c.SeveranceWorldPos.Y);
            }
            c.Armed = true;
            log.Info($"Dismember: clone idx={c.ObjectIndex} armed (limbIdx={c.LimbIndex})");
            return;
        }

        // Armed: re-assert freeze, re-write the frozen limb pose (no breathing), and drive the clone
        // skeleton from the rigid body, pivoting at the limb root so the chunk tumbles about its cut.
        ((Character*)c.Chara)->Timeline.OverallSpeed = 0f;
        if (c.LimbSnapshot != null)
        {
            var pose = skel.Pose;
            foreach (var s in c.LimbSnapshot)
            {
                ref var m = ref pose->ModelPose.Data[s.Idx];
                m.Translation.X = s.T.X; m.Translation.Y = s.T.Y; m.Translation.Z = s.T.Z;
                m.Rotation.X = s.R.X; m.Rotation.Y = s.R.Y; m.Rotation.Z = s.R.Z; m.Rotation.W = s.R.W;
                m.Scale.X = s.S.X; m.Scale.Y = s.S.Y; m.Scale.Z = s.S.Z;
            }
        }
        if (simulation == null || c.Body == null) return;
        var bodyRef = simulation.Bodies.GetBodyReference(c.Body.Value);
        var bodyPos = bodyRef.Pose.Position;
        var bodyRot = bodyRef.Pose.Orientation;
        var skelPos = bodyPos - Vector3.Transform(c.LimbRootModelPos, bodyRot);

        var cb = (CharacterBase*)drawObj;
        var sk = cb->Skeleton;
        if (sk != null)
        {
            sk->Transform.Position.X = skelPos.X;
            sk->Transform.Position.Y = skelPos.Y;
            sk->Transform.Position.Z = skelPos.Z;
            sk->Transform.Rotation.X = bodyRot.X;
            sk->Transform.Rotation.Y = bodyRot.Y;
            sk->Transform.Rotation.Z = bodyRot.Z;
            sk->Transform.Rotation.W = bodyRot.W;
        }
        drawObj->Position = skelPos;
        drawObj->Rotation = bodyRot;
        ((GameObject*)c.Chara)->Position = skelPos;
    }

    // Hide the clone's main/off-hand weapons (separate draw objects not affected by collapsing body
    // bones) so they don't float next to the limb.
    private void HideWeapons(Clone c)
    {
        var character = (Character*)c.Chara;
        var main = character->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).DrawObject;
        var off = character->DrawData.Weapon(DrawDataContainer.WeaponSlot.OffHand).DrawObject;
        if (main != null) main->Scale = Vector3.Zero;
        if (off != null) off->Scale = Vector3.Zero;
    }

    private void ApplyDeferredGlamour(Clone c)
    {
        if (c.GlamourBase64 == null || c.GameObjectRef == null || c.GlamourAttemptsLeft <= 0) return;
        if (c.GlamourFramesUntil > 0) { c.GlamourFramesUntil--; return; }
        if (c.GlamourFramesUntil == 0)
        {
            var ok = glamourerIpc.ApplyStateBase64(c.GlamourBase64, c.GameObjectRef.ObjectIndex);
            c.GlamourAttemptsLeft--;
            if (ok) { c.GlamourBase64 = null; log.Info($"Dismember: glamour applied idx={c.ObjectIndex}"); }
            else c.GlamourFramesUntil = 5; // retry
        }
    }

    // Collapse every bone NOT in the limb's subtree to the CUT POINT (the limb root) with exact-0
    // scale, plus any partial skeleton not attached under the limb. Collapsing to the cut (not far
    // away) pinches the shared seam verts at the cut instead of stretching them into a spike; exact-0
    // scale makes the rest of the body cull to a zero-area point. Inverse of HideLimbSubtree.
    private void HideAllButLimb(SkeletonAccess skel, int limbIdx)
    {
        var pose = skel.Pose;
        ref var lm = ref pose->ModelPose.Data[limbIdx];
        var cx = lm.Translation.X;
        var cy = lm.Translation.Y;
        var cz = lm.Translation.Z;

        var n = Math.Min(skel.BoneCount, skel.ParentCount);
        for (int i = 0; i < n; i++)
        {
            if (IsDescendantOrSelf(skel, i, limbIdx)) continue;
            ref var m = ref pose->ModelPose.Data[i];
            m.Translation.X = cx; m.Translation.Y = cy; m.Translation.Z = cz;
            m.Scale.X = 0.0001f; m.Scale.Y = 0.0001f; m.Scale.Z = 0.0001f; // NOT 0 — singular matrix => NaN glitch
        }

        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return;
        for (int ps = 1; ps < skeleton->PartialSkeletonCount; ps++)
        {
            var partial = &skeleton->PartialSkeletons[ps];
            var ppose = partial->GetHavokPose(0);
            if (ppose == null || ppose->Skeleton == null || ppose->ModelInSync == 0) continue;
            var cnt = ppose->ModelPose.Length;
            if (cnt < 1) continue;
            var rootName = ppose->Skeleton->Bones[0].Name.String;
            var mainIdx = boneService.ResolveBoneIndex(skel, rootName ?? "");
            if (mainIdx >= 0 && IsDescendantOrSelf(skel, mainIdx, limbIdx)) continue; // attached under the limb — keep
            // Face/hair share NO verts with the limb, so throwing them far is safe (no seam stretch)
            // and removes the floating "collapsed head" blob that scale-only left behind.
            for (int b = 0; b < cnt; b++)
            {
                ref var m = ref ppose->ModelPose.Data[b];
                m.Translation.X = 0f; m.Translation.Y = -1000f; m.Translation.Z = 0f;
                m.Scale.X = 0.0001f; m.Scale.Y = 0.0001f; m.Scale.Z = 0.0001f;
            }
        }
    }

    private static bool IsDescendantOrSelf(SkeletonAccess skel, int bone, int root)
    {
        var guard = 0;
        while (bone >= 0 && guard++ < 256)
        {
            if (bone == root) return true;
            bone = skel.HavokSkeleton->ParentIndices[bone];
        }
        return false;
    }

    private void DespawnClone(Clone c)
    {
        try
        {
            if (simulation != null)
            {
                if (c.Body.HasValue) simulation.Bodies.Remove(c.Body.Value);
                if (c.GroundTile.HasValue) simulation.Statics.Remove(c.GroundTile.Value);
                if (c.GroundShape.HasValue && bufferPool != null)
                    simulation.Shapes.RemoveAndDispose(c.GroundShape.Value, bufferPool);
            }

            // Only touch game memory while the session is alive (shutdown frees these objects).
            var clientObjMgr = ClientObjectManager.Instance();
            if (clientObjMgr != null && c.ObjectIndex >= 0 && Core.Services.ObjectTable.LocalPlayer != null)
            {
                if (c.Chara != null) ((GameObject*)c.Chara)->DisableDraw();
                clientObjMgr->DeleteObjectByIndex((ushort)c.ObjectIndex, 0);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Dismember: despawn clone idx={c.ObjectIndex} failed");
        }
        allocatedIndices.Remove(c.ObjectIndex);
    }

    private uint FindFreeObjectHint()
    {
        // Window 200-299 (companions use 100-199). Must avoid slots our own live clones hold, or the
        // game's CreateBattleCharacter collides and fails for the 2nd+ limb.
        uint hint = 200;
        while (hint < 300 && allocatedIndices.Contains((int)hint)) hint++;
        return hint;
    }

    private (StaticHandle, TypedIndex) CreateTerrainPatch(float centerX, float centerZ, float aboveY)
    {
        // A flat box floats/clips on slopes, so build a small MESH from a grid of ground raycasts
        // (the limb spawns at body height ~1m up, so raycast from above it). Mirrors WeaponDropController.
        const float radius = 4f;
        const float step = 0.5f;
        var grid = (int)(radius * 2 / step) + 1;
        var ox = centerX - radius;
        var oz = centerZ - radius;

        var patchY = aboveY - 1.5f;
        var anyHit = false;
        if (BGCollisionModule.RaycastMaterialFilter(new Vector3(centerX, aboveY + 5f, centerZ), new Vector3(0, -1, 0), out var ch, 80f))
        { patchY = ch.Point.Y; anyHit = true; }

        var heights = new float[grid, grid];
        for (int gz = 0; gz < grid; gz++)
        for (int gx = 0; gx < grid; gx++)
        {
            var wx = ox + gx * step; var wz = oz + gz * step;
            if (BGCollisionModule.RaycastMaterialFilter(new Vector3(wx, patchY + 5f, wz), new Vector3(0, -1, 0), out var gh, 80f))
            { heights[gx, gz] = gh.Point.Y; anyHit = true; }
            else heights[gx, gz] = patchY;
        }

        if (!anyHit)
        {
            var box = simulation!.Shapes.Add(new Box(8f, 0.1f, 8f));
            var bst = simulation.Statics.Add(new StaticDescription(new Vector3(centerX, patchY - 0.05f, centerZ), Quaternion.Identity, box));
            return (bst, box);
        }

        var triCount = (grid - 1) * (grid - 1) * 2;
        bufferPool!.Take<Triangle>(triCount, out var tris);
        var ti = 0;
        for (int gz = 0; gz < grid - 1; gz++)
        for (int gx = 0; gx < grid - 1; gx++)
        {
            var x0 = ox + gx * step; var x1 = x0 + step;
            var z0 = oz + gz * step; var z1 = z0 + step;
            var v00 = new Vector3(x0, heights[gx, gz], z0);
            var v10 = new Vector3(x1, heights[gx + 1, gz], z0);
            var v01 = new Vector3(x0, heights[gx, gz + 1], z1);
            var v11 = new Vector3(x1, heights[gx + 1, gz + 1], z1);
            tris[ti++] = new Triangle(v00, v10, v01);
            tris[ti++] = new Triangle(v10, v11, v01);
        }
        var mesh = new BepuPhysics.Collidables.Mesh(tris, Vector3.One, bufferPool);
        var shape = simulation!.Shapes.Add(mesh);
        var st = simulation.Statics.Add(new StaticDescription(Vector3.Zero, Quaternion.Identity, shape));
        return (st, shape);
    }

    private void EnsureSimulation()
    {
        if (simulation != null) return;
        bufferPool = new BufferPool();
        simulation = BepuSimulation.Create(
            bufferPool,
            new WeaponDropNarrowPhaseCallbacks { Friction = config.RagdollFriction, MaxRecoveryVelocity = 1.5f },
            // Stronger damping than weapons (0.97/0.92) so limbs tumble a bit then settle, not roll far.
            new WeaponDropPoseIntegratorCallbacks(new Vector3(0, -config.RagdollGravity, 0), 0.93f, 0.84f),
            new SolveDescription(4, 1));

        var shape = new Capsule(LimbRadius, LimbHalfLength * 2f);
        limbShapeIndex = simulation.Shapes.Add(shape);
        limbInertia = shape.ComputeInertia(LimbMass);
        log.Info("DismembermentController: simulation created");
    }

    private void DestroySimulation()
    {
        simulation?.Dispose();
        simulation = null;
        bufferPool?.Clear();
        bufferPool = null;
    }

    public void Dispose()
    {
        boneService.OnRenderFrame -= OnRenderFrame;
        RemoveAll();
        DestroySimulation();
    }
}
