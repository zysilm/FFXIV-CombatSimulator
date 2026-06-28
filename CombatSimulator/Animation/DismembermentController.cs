using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
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
    private readonly AnimationController animationController;
    private readonly IObjectTable objectTable;
    private readonly Configuration config;
    private readonly IPluginLog log;

    // Limb physics proxy (one capsule shape shared by all limb bodies). Generic limb-sized chunk.
    private const float LimbRadius = 0.06f;
    private const float LimbHalfLength = 0.14f;
    private const float LimbMass = 4f;
    private const int MaxPendingFrames = 120;

    private BufferPool? bufferPool;
    private BepuSimulation? simulation;
    private readonly HashSet<(int, int)> connectedPairs = new();
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
        public List<TypedIndex>? Shapes; // compound + child shapes to dispose
        public BodyHandle? Body;
        public LimbRig? Rig;
        public StaticHandle? GroundTile;
        public TypedIndex? GroundShape;
        public bool KeepTimelineRunning;
        public string? GlamourBase64;
        public int GlamourFramesUntil = -1;
        public int GlamourAttemptsLeft;
    }

    private sealed class LimbRig
    {
        public readonly List<LimbBody> Bodies = new();
        public readonly HashSet<int> BoneIndices = new();
        public readonly List<(int, int)> ConnectedPairs = new();
        public readonly List<TypedIndex> Shapes = new();
    }

    private struct LimbBody
    {
        public int BoneIndex;
        public string Name;
        public BodyHandle Body;
        public Quaternion BodyToBoneRotation;
        public float SegmentHalfLength;
        public RagdollController.RagdollBoneDef Def;
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
        AnimationController animationController, IObjectTable objectTable, Configuration config, IPluginLog log)
    {
        this.boneService = boneService;
        this.glamourerIpc = glamourerIpc;
        this.animationController = animationController;
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
        WriteCloneName((GameObject*)obj, index);

        const CharacterSetupContainer.CopyFlags flags =
            CharacterSetupContainer.CopyFlags.ClassJob | CharacterSetupContainer.CopyFlags.WeaponHiding;
        character->CharacterSetup.CopyFromCharacter(src, flags);
        character->CharacterSetup.CopyFromCharacter(character, CharacterSetupContainer.CopyFlags.None);
        WriteCloneName((GameObject*)obj, index);
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
        c.GlamourFramesUntil = 1;  // apply WHILE the clone is still alive (frozen actors may not redraw)
        c.GlamourAttemptsLeft = 20;
        if (IsHeadLimb(c.LimbRootBone))
        {
            c.KeepTimelineRunning = true;
            animationController.PlayDeathAnimationOnActor((Character*)c.Chara, forceCombatDeath: true);
        }
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
            ((Character*)c.Chara)->Timeline.OverallSpeed = c.KeepTimelineRunning ? 1f : 0f;
            if (!c.KeepTimelineRunning)
            {
                // Snapshot ordinary limb poses so animation cannot keep moving the prop after arming.
                // Head clones are deliberately excluded: their subtree contains ears/hair/face-related
                // bones that must remain under the active timeline to avoid fighting the client pose.
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
            }
            EnsureSimulation();
            if (simulation != null)
            {
                if (c.KeepTimelineRunning)
                {
                    // Head keeps the older single-proxy path. Building a rig from the head subtree
                    // pulls in cosmetic partials and modded ear/hair bones that should stay visual.
                    c.Body = simulation.Bodies.Add(BodyDescription.CreateDynamic(
                        new RigidPose(c.SeveranceWorldPos, c.SeveranceWorldRot),
                        default(BodyVelocity),
                        limbInertia,
                        new CollidableDescription(limbShapeIndex, 0.04f),
                        new BodyActivityDescription(0.01f)));
                }
                else
                {
                    c.Rig = BuildLimbRig(skel, c);
                    if (c.Rig == null)
                    {
                        var shape = BuildLimbShape(skel, c, out var inertia);
                        c.Body = simulation.Bodies.Add(BodyDescription.CreateDynamic(
                            new RigidPose(c.SeveranceWorldPos, c.SeveranceWorldRot),
                            default(BodyVelocity),
                            inertia,
                            new CollidableDescription(shape, 0.04f),
                            new BodyActivityDescription(0.01f)));
                    }
                }
                (c.GroundTile, c.GroundShape) = CreateTerrainPatch(c.SeveranceWorldPos.X, c.SeveranceWorldPos.Z, c.SeveranceWorldPos.Y);
            }
            c.Armed = true;
            log.Info($"Dismember: clone idx={c.ObjectIndex} armed (limbIdx={c.LimbIndex})");
            return;
        }

        // Armed: re-assert freeze, re-write the frozen limb pose (no breathing), and drive the clone
        // skeleton from the rigid body, pivoting at the limb root so the chunk tumbles about its cut.
        ((Character*)c.Chara)->Timeline.OverallSpeed = c.KeepTimelineRunning ? 1f : 0f;
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
        if (simulation == null) return;
        if (c.Rig != null)
        {
            DriveLimbRig(skel, c);
            HideAllButLimb(skel, c.LimbIndex);
            HideWeapons(c);
            return;
        }
        if (c.Body == null) return;
        var bodyRef = simulation.Bodies.GetBodyReference(c.Body.Value);
        var bodyPos = bodyRef.Pose.Position;
        var bodyRot = bodyRef.Pose.Orientation;
        if (c.KeepTimelineRunning)
        {
            ref var liveRoot = ref skel.Pose->ModelPose.Data[c.LimbIndex];
            c.LimbRootModelPos = new Vector3(liveRoot.Translation.X, liveRoot.Translation.Y, liveRoot.Translation.Z);
        }
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

    private LimbRig? BuildLimbRig(SkeletonAccess skel, Clone c)
    {
        if (simulation == null) return null;
        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return null;

        var skelPos = new Vector3(
            skeleton->Transform.Position.X,
            skeleton->Transform.Position.Y,
            skeleton->Transform.Position.Z);
        var skelRot = Quaternion.Normalize(new Quaternion(
            skeleton->Transform.Rotation.X,
            skeleton->Transform.Rotation.Y,
            skeleton->Transform.Rotation.Z,
            skeleton->Transform.Rotation.W));

        var defs = GetRagdollBoneDefs();
        var selected = new List<RagdollController.RagdollBoneDef>();
        var selectedNames = new HashSet<string>();
        foreach (var def in defs)
        {
            if (!IsLimbRigRole(def.AnatomicalRole) || def.SoftBody)
                continue;
            var idx = boneService.ResolveBoneIndex(skel, def.Name);
            if (idx < 0 || !IsDescendantOrSelf(skel, idx, c.LimbIndex))
                continue;
            selected.Add(def);
            selectedNames.Add(def.Name);
        }

        if (selected.Count < 2)
            return null;

        var rig = new LimbRig();
        var bodyByName = new Dictionary<string, LimbBody>();
        var worldPositions = new Dictionary<string, Vector3>();
        var worldRotations = new Dictionary<string, Quaternion>();

        foreach (var def in selected)
        {
            var idx = boneService.ResolveBoneIndex(skel, def.Name);
            ref var mt = ref skel.Pose->ModelPose.Data[idx];
            var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            var modelRot = Quaternion.Normalize(new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W));
            worldPositions[def.Name] = skelPos + Vector3.Transform(modelPos, skelRot);
            worldRotations[def.Name] = Quaternion.Normalize(skelRot * modelRot);
        }

        foreach (var def in selected)
        {
            var idx = boneService.ResolveBoneIndex(skel, def.Name);
            var boneWorldPos = worldPositions[def.Name];
            var boneWorldRot = worldRotations[def.Name];

            var segment = Vector3.Zero;
            var hasSegment = false;
            var childName = FindSelectedChild(def.Name, selected);
            if (childName != null && worldPositions.TryGetValue(childName, out var childWorldPos))
            {
                segment = childWorldPos - boneWorldPos;
                hasSegment = segment.LengthSquared() > 1e-6f;
            }
            else if (def.ParentName != null && worldPositions.TryGetValue(def.ParentName, out var parentWorldPos))
            {
                segment = boneWorldPos - parentWorldPos;
                hasSegment = segment.LengthSquared() > 1e-6f;
            }

            var bodyHalfLength = ResolveBodyHalfLength(def);
            var bodyCenter = boneWorldPos;
            var bodyRot = boneWorldRot;
            var segmentHalfLength = 0f;
            if (hasSegment)
            {
                var segmentLength = segment.Length();
                var axis = segment / segmentLength;
                segmentHalfLength = MathF.Min(bodyHalfLength, MathF.Max(0f, segmentLength * 0.45f));
                bodyCenter = boneWorldPos + axis * segmentHalfLength;
                bodyRot = CreateCapsuleRotation(segment, boneWorldRot);
            }

            var mass = MathF.Max(0.05f, def.Mass);
            var shapeIndex = CreateRigShape(def, bodyHalfLength, mass, out var inertia);
            rig.Shapes.Add(shapeIndex);

            var handle = simulation.Bodies.Add(BodyDescription.CreateDynamic(
                new RigidPose(bodyCenter, bodyRot),
                default(BodyVelocity),
                inertia,
                new CollidableDescription(shapeIndex, 0.04f),
                new BodyActivityDescription(0.01f)));

            var rb = new LimbBody
            {
                BoneIndex = idx,
                Name = def.Name,
                Body = handle,
                BodyToBoneRotation = Quaternion.Normalize(Quaternion.Inverse(bodyRot) * boneWorldRot),
                SegmentHalfLength = segmentHalfLength,
                Def = def,
            };
            rig.Bodies.Add(rb);
            rig.BoneIndices.Add(idx);
            bodyByName[def.Name] = rb;
        }

        foreach (var rb in rig.Bodies)
        {
            if (rb.Def.ParentName == null || !bodyByName.TryGetValue(rb.Def.ParentName, out var parent))
                continue;

            rig.ConnectedPairs.Add(AddConnectedPair(rb.Body, parent.Body));
            if (parent.Def.ParentName != null && bodyByName.TryGetValue(parent.Def.ParentName, out var grandParent))
                rig.ConnectedPairs.Add(AddConnectedPair(rb.Body, grandParent.Body));

            AddLimbRigConstraint(rb, parent, worldPositions);
        }

        log.Info($"Dismember: local rig idx={c.ObjectIndex} bone={c.LimbRootBone} bodies={rig.Bodies.Count}");
        return rig;
    }

    private void DriveLimbRig(SkeletonAccess skel, Clone c)
    {
        if (simulation == null || c.Rig == null) return;
        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return;

        var skelPos = new Vector3(
            skeleton->Transform.Position.X,
            skeleton->Transform.Position.Y,
            skeleton->Transform.Position.Z);
        var skelRot = Quaternion.Normalize(new Quaternion(
            skeleton->Transform.Rotation.X,
            skeleton->Transform.Rotation.Y,
            skeleton->Transform.Rotation.Z,
            skeleton->Transform.Rotation.W));
        var skelRotInv = Quaternion.Inverse(skelRot);

        var result = new BoneModificationResult(skel.BoneCount);
        for (int i = 0; i < skel.BoneCount; i++)
        {
            ref var m = ref skel.Pose->ModelPose.Data[i];
            result.OriginalPositions[i] = new Vector3(m.Translation.X, m.Translation.Y, m.Translation.Z);
            result.OriginalRotations[i] = Quaternion.Normalize(new Quaternion(m.Rotation.X, m.Rotation.Y, m.Rotation.Z, m.Rotation.W));
        }

        foreach (var rb in c.Rig.Bodies)
        {
            var body = simulation.Bodies.GetBodyReference(rb.Body);
            var boneWorldRot = Quaternion.Normalize(body.Pose.Orientation * rb.BodyToBoneRotation);
            var boneWorldPos = body.Pose.Position;
            if (rb.SegmentHalfLength > 0)
            {
                var yAxis = Vector3.Transform(Vector3.UnitY, body.Pose.Orientation);
                boneWorldPos -= yAxis * rb.SegmentHalfLength;
            }

            var modelPos = Vector3.Transform(boneWorldPos - skelPos, skelRotInv);
            var modelRot = Quaternion.Normalize(skelRotInv * boneWorldRot);
            boneService.WriteBoneTransform(skel, rb.BoneIndex, modelPos, modelRot, result);
        }

        for (int i = 0; i < skel.BoneCount && i < skel.ParentCount; i++)
        {
            if (c.Rig.BoneIndices.Contains(i) || !IsDescendantOrSelf(skel, i, c.LimbIndex))
                continue;

            var parentIdx = skel.HavokSkeleton->ParentIndices[i];
            if (parentIdx < 0 || !result.HasAccumulated[parentIdx])
                continue;

            var parentDelta = result.AccumulatedDeltas[parentIdx];
            var parentOrigPos = result.OriginalPositions[parentIdx];
            ref var parentModel = ref skel.Pose->ModelPose.Data[parentIdx];
            var parentNewPos = new Vector3(parentModel.Translation.X, parentModel.Translation.Y, parentModel.Translation.Z);

            var relPos = result.OriginalPositions[i] - parentOrigPos;
            relPos = Vector3.Transform(relPos, parentDelta);
            var newPos = parentOrigPos + relPos + (parentNewPos - parentOrigPos);
            var newRot = Quaternion.Normalize(parentDelta * result.OriginalRotations[i]);

            boneService.WriteBoneTransform(skel, i, newPos, newRot, result);
        }
    }

    private RagdollController.RagdollBoneDef[] GetRagdollBoneDefs()
    {
        if (config.RagdollBoneConfigs.Count > 0)
            return RagdollController.BuildBoneDefsFromConfigs(config.RagdollBoneConfigs.ToArray());
        return RagdollController.DefaultBoneDefs;
    }

    private static bool IsLimbRigRole(RagdollController.AnatomicalRole role)
        => role is RagdollController.AnatomicalRole.Shoulder
            or RagdollController.AnatomicalRole.Elbow
            or RagdollController.AnatomicalRole.Hand
            or RagdollController.AnatomicalRole.Hip
            or RagdollController.AnatomicalRole.Knee
            or RagdollController.AnatomicalRole.Ankle
            or RagdollController.AnatomicalRole.Foot;

    private static string? FindSelectedChild(string parentName, List<RagdollController.RagdollBoneDef> defs)
    {
        foreach (var def in defs)
            if (string.Equals(def.ParentName, parentName, StringComparison.Ordinal))
                return def.Name;
        return null;
    }

    private TypedIndex CreateRigShape(RagdollController.RagdollBoneDef def, float bodyHalfLength, float mass, out BodyInertia inertia)
    {
        if (def.ColliderShape == RagdollController.RagdollColliderShape.Box)
        {
            var extents = ResolveBoxHalfExtents(def, bodyHalfLength);
            var box = new Box(extents.X * 2f, extents.Y * 2f, extents.Z * 2f);
            inertia = box.ComputeInertia(mass);
            return simulation!.Shapes.Add(box);
        }

        var capsule = new Capsule(MathF.Max(0.005f, def.CapsuleRadius), bodyHalfLength * 2f);
        inertia = capsule.ComputeInertia(mass);
        return simulation!.Shapes.Add(capsule);
    }

    private void AddLimbRigConstraint(LimbBody child, LimbBody parent, Dictionary<string, Vector3> worldPositions)
    {
        if (simulation == null) return;
        var childBody = simulation.Bodies.GetBodyReference(child.Body);
        var parentBody = simulation.Bodies.GetBodyReference(parent.Body);
        var anchorWorld = worldPositions.TryGetValue(child.Name, out var anchor) ? anchor : childBody.Pose.Position;

        var childLocalAnchor = Vector3.Transform(anchorWorld - childBody.Pose.Position, Quaternion.Inverse(childBody.Pose.Orientation));
        var parentLocalAnchor = Vector3.Transform(anchorWorld - parentBody.Pose.Position, Quaternion.Inverse(parentBody.Pose.Orientation));

        var childSeg = NormalizeOrFallback(Vector3.Transform(Vector3.UnitY, childBody.Pose.Orientation), Vector3.UnitY);
        var parentSeg = NormalizeOrFallback(Vector3.Transform(Vector3.UnitY, parentBody.Pose.Orientation), Vector3.UnitY);
        var jointSpring = new SpringSettings(18f, 1f);
        var limitSpring = new SpringSettings(10f, 1f);

        simulation.Solver.Add(child.Body, parent.Body, new BallSocket
        {
            LocalOffsetA = childLocalAnchor,
            LocalOffsetB = parentLocalAnchor,
            SpringSettings = jointSpring,
        });

        if (child.Def.Joint == RagdollController.JointType.Hinge)
        {
            var hingeAxis = ComputeHingeAxis(childSeg);
            var forward = ComputeHingeForward(hingeAxis, parentSeg, childSeg);

            simulation.Solver.Add(child.Body, parent.Body, new SwingLimit
            {
                AxisLocalA = Vector3.Normalize(Vector3.Transform(childSeg, Quaternion.Inverse(childBody.Pose.Orientation))),
                AxisLocalB = Vector3.Normalize(Vector3.Transform(forward, Quaternion.Inverse(parentBody.Pose.Orientation))),
                MaximumSwingAngle = MathF.Max(0.05f, child.Def.SwingLimit),
                SpringSettings = limitSpring,
            });

            simulation.Solver.Add(child.Body, parent.Body, new AngularHinge
            {
                LocalHingeAxisA = Vector3.Normalize(Vector3.Transform(hingeAxis, Quaternion.Inverse(childBody.Pose.Orientation))),
                LocalHingeAxisB = Vector3.Normalize(Vector3.Transform(hingeAxis, Quaternion.Inverse(parentBody.Pose.Orientation))),
                SpringSettings = new SpringSettings(8f, 1f),
            });
        }
        else if (child.Def.SwingLimit > 0)
        {
            simulation.Solver.Add(child.Body, parent.Body, new SwingLimit
            {
                AxisLocalA = Vector3.Normalize(Vector3.Transform(childSeg, Quaternion.Inverse(childBody.Pose.Orientation))),
                AxisLocalB = Vector3.Normalize(Vector3.Transform(childSeg, Quaternion.Inverse(parentBody.Pose.Orientation))),
                MaximumSwingAngle = child.Def.SwingLimit,
                SpringSettings = limitSpring,
            });
        }

        if (child.Def.TwistMinAngle != 0 || child.Def.TwistMaxAngle != 0)
        {
            var refDir = ComputeTwistReference(childSeg);
            var twistBasis = CreateTwistBasis(childSeg, refDir);
            simulation.Solver.Add(child.Body, parent.Body, new TwistLimit
            {
                LocalBasisA = Quaternion.Normalize(Quaternion.Inverse(childBody.Pose.Orientation) * twistBasis),
                LocalBasisB = Quaternion.Normalize(Quaternion.Inverse(parentBody.Pose.Orientation) * twistBasis),
                MinimumAngle = child.Def.TwistMinAngle,
                MaximumAngle = child.Def.TwistMaxAngle,
                SpringSettings = limitSpring,
            });
        }

        simulation.Solver.Add(child.Body, parent.Body, new AngularMotor
        {
            TargetVelocityLocalA = Vector3.Zero,
            Settings = new MotorSettings(float.MaxValue, 0.25f),
        });
    }

    private (int, int) AddConnectedPair(BodyHandle a, BodyHandle b)
    {
        var lo = Math.Min(a.Value, b.Value);
        var hi = Math.Max(a.Value, b.Value);
        var pair = (lo, hi);
        connectedPairs.Add(pair);
        return pair;
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
        if (c.GlamourBase64 == null || c.GlamourAttemptsLeft <= 0) return;
        if (c.GlamourFramesUntil > 0) { c.GlamourFramesUntil--; return; }
        if (c.GlamourFramesUntil == 0)
        {
            var objectIndex = (int)((GameObject*)c.Chara)->ObjectIndex;
            var ok = glamourerIpc.ApplyStateBase64(c.GlamourBase64, objectIndex);
            c.GlamourAttemptsLeft--;
            if (ok)
            {
                c.GlamourBase64 = null;
                if (!c.Armed)
                    c.SettleFrames = Math.Max(c.SettleFrames, 6);
                log.Info($"Dismember: glamour applied idx={objectIndex}");
            }
            else c.GlamourFramesUntil = 5; // retry
        }
    }

    private static bool IsHeadLimb(string limbRootBone)
        => string.Equals(limbRootBone, "j_kao", StringComparison.OrdinalIgnoreCase);

    private static void WriteCloneName(GameObject* obj, int objectIndex)
    {
        var suffix = Math.Abs(objectIndex) % (26 * 26);
        var first = (char)('A' + suffix / 26);
        var second = (char)('a' + suffix % 26);
        var nameBytes = Encoding.UTF8.GetBytes($"Mirror {first}{second}");
        for (int j = 0; j < 64; j++)
            obj->Name[j] = j < nameBytes.Length && j < 63 ? nameBytes[j] : (byte)0;
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
                if (c.Rig != null)
                {
                    foreach (var pair in c.Rig.ConnectedPairs)
                        connectedPairs.Remove(pair);
                    foreach (var rb in c.Rig.Bodies)
                        try { simulation.Bodies.Remove(rb.Body); } catch { }
                    if (bufferPool != null)
                        foreach (var s in c.Rig.Shapes)
                            try { simulation.Shapes.RemoveAndDispose(s, bufferPool); } catch { }
                }
                if (c.GroundTile.HasValue) simulation.Statics.Remove(c.GroundTile.Value);
                if (c.GroundShape.HasValue && bufferPool != null)
                    simulation.Shapes.RemoveAndDispose(c.GroundShape.Value, bufferPool);
                if (c.Shapes != null && bufferPool != null)
                    foreach (var s in c.Shapes)
                        try { simulation.Shapes.RemoveAndDispose(s, bufferPool); } catch { }
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

    // Build a Bepu Compound matching the limb's real shape: one capsule per bone->child segment in
    // the snapshot (so a whole arm = upper-arm + forearm), or a single blob for the head. Children are
    // in the limb-root frame, so the body origin stays the limb root and the pivot drive is unchanged.
    private TypedIndex BuildLimbShape(SkeletonAccess skel, Clone c, out BodyInertia inertia)
    {
        var snap = c.LimbSnapshot!;
        var rootRot = Quaternion.Identity;
        foreach (var s in snap) if (s.Idx == c.LimbIndex) { rootRot = Quaternion.Normalize(s.R); break; }
        var rootInv = Quaternion.Conjugate(rootRot);
        var root = c.LimbRootModelPos;
        var rad = LimbSegmentRadius(c.LimbRootBone);

        var segs = new List<(Vector3 C, Quaternion O, float Len)>();
        var maxReach = 0f;
        foreach (var s in snap)
        foreach (var t in snap)
        {
            if (t.Idx == s.Idx) continue;
            if (skel.HavokSkeleton->ParentIndices[t.Idx] != s.Idx) continue;
            var lb = Vector3.Transform(s.T - root, rootInv);
            var lc = Vector3.Transform(t.T - root, rootInv);
            var dir = lc - lb; var len = dir.Length();
            if (len < 0.02f) continue;
            var center = (lb + lc) * 0.5f;
            segs.Add((center, AlignYTo(dir / len), len));
            maxReach = MathF.Max(maxReach, center.Length() + len * 0.5f);
        }
        if (segs.Count == 0) // head / single-bone: a small blob at the root
            segs.Add((Vector3.Zero, Quaternion.Identity, 0.05f));

        bufferPool!.Take<CompoundChild>(segs.Count, out var cbuf);
        c.Shapes = new List<TypedIndex>();
        for (int i = 0; i < segs.Count; i++)
        {
            var idx = simulation!.Shapes.Add(new Capsule(rad, segs[i].Len));
            c.Shapes.Add(idx);
            cbuf[i] = new CompoundChild { ShapeIndex = idx, LocalPosition = segs[i].C, LocalOrientation = segs[i].O };
        }
        var compoundIdx = simulation!.Shapes.Add(new Compound(cbuf));
        c.Shapes.Add(compoundIdx);
        inertia = new Capsule(rad, MathF.Max(0.12f, maxReach)).ComputeInertia(LimbMass);
        return compoundIdx;
    }

    private static float LimbSegmentRadius(string bone)
        => bone.StartsWith("j_asi", StringComparison.Ordinal) ? 0.065f : bone == "j_kao" ? 0.09f : 0.045f;

    private static Quaternion AlignYTo(Vector3 dir)
    {
        var d = Vector3.Dot(Vector3.UnitY, dir);
        if (d > 0.9999f) return Quaternion.Identity;
        if (d < -0.9999f) return new Quaternion(1f, 0f, 0f, 0f); // 180° about X
        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, dir));
        return Quaternion.CreateFromAxisAngle(axis, MathF.Acos(Math.Clamp(d, -1f, 1f)));
    }

    private static float ResolveBodyHalfLength(RagdollController.RagdollBoneDef def)
    {
        if (def.ColliderShape == RagdollController.RagdollColliderShape.Box && def.BoxHalfExtents.Y > 0)
            return def.BoxHalfExtents.Y;
        return MathF.Max(0f, def.CapsuleHalfLength);
    }

    private static Vector3 ResolveBoxHalfExtents(RagdollController.RagdollBoneDef def, float bodyHalfLength)
    {
        var x = def.BoxHalfExtents.X > 0 ? def.BoxHalfExtents.X : MathF.Max(0.01f, def.CapsuleRadius);
        var y = def.BoxHalfExtents.Y > 0 ? def.BoxHalfExtents.Y : MathF.Max(0.01f, bodyHalfLength);
        var z = def.BoxHalfExtents.Z > 0 ? def.BoxHalfExtents.Z : MathF.Max(0.01f, def.CapsuleRadius);
        return new Vector3(x, y, z);
    }

    private static Quaternion CreateCapsuleRotation(Vector3 segmentDir, Quaternion boneWorldRot)
    {
        var y = NormalizeOrFallback(segmentDir, Vector3.UnitY);
        var z = ProjectOntoPlane(Vector3.Transform(Vector3.UnitZ, boneWorldRot), y);
        if (z.LengthSquared() < 1e-5f)
            z = ProjectOntoPlane(Vector3.Transform(Vector3.UnitX, boneWorldRot), y);
        if (z.LengthSquared() < 1e-5f)
            z = ProjectOntoPlane(MathF.Abs(Vector3.Dot(y, Vector3.UnitY)) > 0.9f ? Vector3.UnitZ : Vector3.UnitY, y);

        z = NormalizeOrFallback(z, Vector3.UnitZ);
        var x = NormalizeOrFallback(Vector3.Cross(y, z), Vector3.UnitX);
        z = NormalizeOrFallback(Vector3.Cross(x, y), z);

        var m = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    private static Quaternion CreateTwistBasis(Vector3 twistAxis, Vector3 referenceDir)
    {
        var z = NormalizeOrFallback(twistAxis, Vector3.UnitY);
        var y = NormalizeOrFallback(Vector3.Cross(z, referenceDir), Vector3.UnitX);
        var x = NormalizeOrFallback(Vector3.Cross(y, z), Vector3.UnitZ);
        var m = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    private static Vector3 ComputeHingeAxis(Vector3 segmentDir)
    {
        var seg = NormalizeOrFallback(segmentDir, Vector3.UnitY);
        var axis = Vector3.Cross(seg, Vector3.UnitY);
        if (axis.LengthSquared() < 0.001f)
            axis = Vector3.Cross(seg, Vector3.UnitZ);
        return NormalizeOrFallback(axis, Vector3.UnitX);
    }

    private static Vector3 ComputeHingeForward(Vector3 hingeAxis, Vector3 parentSegmentDir, Vector3 childSegmentDir)
    {
        var parent = NormalizeOrFallback(parentSegmentDir, Vector3.UnitY);
        var child = NormalizeOrFallback(childSegmentDir, Vector3.UnitY);
        var forward = Vector3.Cross(hingeAxis, parent);
        if (forward.LengthSquared() < 0.001f)
            forward = ProjectOntoPlane(child, hingeAxis);
        forward = NormalizeOrFallback(forward, child);
        return Vector3.Dot(forward, child) < 0 ? -forward : forward;
    }

    private static Vector3 ComputeTwistReference(Vector3 segmentDir)
    {
        var seg = NormalizeOrFallback(segmentDir, Vector3.UnitY);
        var reference = Vector3.Cross(seg, Vector3.UnitY);
        if (reference.LengthSquared() < 0.001f)
            reference = Vector3.Cross(seg, Vector3.UnitX);
        return NormalizeOrFallback(reference, Vector3.UnitX);
    }

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 1e-6f)
            return Vector3.Normalize(value);
        return fallback.LengthSquared() > 1e-6f ? Vector3.Normalize(fallback) : Vector3.UnitY;
    }

    private static Vector3 ProjectOntoPlane(Vector3 value, Vector3 planeNormal)
    {
        var normal = NormalizeOrFallback(planeNormal, Vector3.UnitY);
        return value - Vector3.Dot(value, normal) * normal;
    }

    private void EnsureSimulation()
    {
        if (simulation != null) return;
        bufferPool = new BufferPool();
        simulation = BepuSimulation.Create(
            bufferPool,
            new RagdollNarrowPhaseCallbacks { ConnectedPairs = connectedPairs, Friction = config.RagdollFriction },
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
        connectedPairs.Clear();
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
