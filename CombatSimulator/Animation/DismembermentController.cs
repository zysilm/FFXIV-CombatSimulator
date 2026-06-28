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
        public int LimbIndex = -1;
        public Vector3 LimbRootModelPos;
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

            // Draw-ready poll + freeze + body creation.
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
                DriveClone(c);
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
        var obj = clientObjMgr->GetObjectByIndex((ushort)index);
        if (obj == null) { log.Warning("Dismember: object null after create"); return; }

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
        ((Character*)c.Chara)->Timeline.OverallSpeed = 0f; // freeze pose

        var skelN = boneService.TryGetSkeleton((nint)c.Chara);
        if (skelN == null) return;
        var skel = skelN.Value;
        c.LimbIndex = boneService.ResolveBoneIndex(skel, c.LimbRootBone);
        if (c.LimbIndex >= 0)
        {
            ref var lm = ref skel.Pose->ModelPose.Data[c.LimbIndex];
            c.LimbRootModelPos = new Vector3(lm.Translation.X, lm.Translation.Y, lm.Translation.Z);
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

        c.DrawEnabled = true;
        c.GlamourFramesUntil = 5;
        c.GlamourAttemptsLeft = 4;
        log.Info($"Dismember: clone idx={c.ObjectIndex} drawn + frozen, body created (limbIdx={c.LimbIndex})");
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

    private void DriveClone(Clone c)
    {
        if (simulation == null || c.Body == null || c.Chara == null || c.LimbIndex < 0) return;
        var drawObj = ((GameObject*)c.Chara)->DrawObject;
        if (drawObj == null) return;

        // Re-assert the freeze (the game recomputes it each frame).
        ((Character*)c.Chara)->Timeline.OverallSpeed = 0f;

        // Hide everything EXCEPT the limb subtree, each frame (the clone isn't ragdolled, so nothing
        // else keeps these collapsed).
        var skelN = boneService.TryGetSkeleton((nint)c.Chara);
        if (skelN == null) return;
        var skel = skelN.Value;
        HideAllButLimb(skel, c.LimbIndex, c.LimbRootModelPos);

        // Drive the clone skeleton root from the rigid body, pivoting at the limb root so the visible
        // chunk tumbles about its cut end.
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

    // Collapse every bone NOT in the limb's subtree to the cut point + ~0 scale, plus any partial
    // skeleton not attached under the limb. Inverse of RagdollController.HideLimbSubtree.
    private void HideAllButLimb(SkeletonAccess skel, int limbIdx, Vector3 cut)
    {
        var pose = skel.Pose;
        var n = Math.Min(skel.BoneCount, skel.ParentCount);
        for (int i = 0; i < n; i++)
        {
            if (IsDescendantOrSelf(skel, i, limbIdx)) continue;
            ref var m = ref pose->ModelPose.Data[i];
            m.Translation.X = cut.X; m.Translation.Y = cut.Y; m.Translation.Z = cut.Z;
            m.Scale.X = 0.0001f; m.Scale.Y = 0.0001f; m.Scale.Z = 0.0001f;
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
            for (int b = 0; b < cnt; b++)
            {
                ref var m = ref ppose->ModelPose.Data[b];
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
    }

    private uint FindFreeObjectHint()
    {
        // Use a different window than companions (100-199) to reduce hint collisions.
        uint hint = 200;
        while (hint < 250) hint++;
        return 200;
    }

    private (StaticHandle, TypedIndex) CreateTerrainPatch(float centerX, float centerZ, float defaultGroundY)
    {
        // Reuse the weapon-drop flat-fallback approach (a small box) — limbs are small and roll on the
        // local patch; a flat box is adequate for the POC and avoids the mesh-buffer cleanup path.
        var shape = simulation!.Shapes.Add(new Box(8f, 0.1f, 8f));
        var st = simulation.Statics.Add(new StaticDescription(
            new Vector3(centerX, defaultGroundY - 0.05f, centerZ), Quaternion.Identity, shape));
        return (st, shape);
    }

    private void EnsureSimulation()
    {
        if (simulation != null) return;
        bufferPool = new BufferPool();
        simulation = BepuSimulation.Create(
            bufferPool,
            new WeaponDropNarrowPhaseCallbacks { Friction = config.RagdollFriction, MaxRecoveryVelocity = 1.5f },
            new WeaponDropPoseIntegratorCallbacks(new Vector3(0, -config.RagdollGravity, 0), 0.97f, 0.92f),
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
