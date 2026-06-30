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
using RenderModel = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model;

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
    // A clone is drawn (per the original timing) but its limb bone may not resolve immediately
    // on a cold model load. Keep it hidden until the bone appears; only after this many frames
    // (~10s) do we conclude the wrong/placeholder model was built and drop the clone.
    private const int MaxResolveFrames = 600;
    private const uint CloneSlotStart = 200;
    private const uint CloneSlotEndExclusive = 249;
    private const int ReservedPlayerCloneSlots = 8;
    private const int CloneSlotReuseCooldownFrames = 30;

    private BufferPool? bufferPool;
    private BepuSimulation? simulation;
    private readonly HashSet<(int, int)> connectedPairs = new();
    private readonly HashSet<int> restrictedStaticHandles = new();
    private readonly HashSet<int> pcCollisionBodyHandles = new();
    private readonly List<NpcCollisionState> pcNpcCollisionStates = new();
    private readonly HashSet<nint> pcNpcCollisionAddresses = new();
    private TypedIndex limbShapeIndex;
    private BodyInertia limbInertia;
    private uint nextEntityId = 0xF2000001;
    private long nextCloneSeq;
    private readonly Queue<uint> freeCloneSlots = new();
    private readonly Queue<(uint Slot, int Frames)> coolingCloneSlots = new();
    private readonly HashSet<int> coolingCloneSlotSet = new();
    private bool cloneSlotQueueInitialized;

    public float DismemberActivationImpulse { get; set; }
    public bool EnablePcDismemberNpcCollision { get; set; }
    public Func<IReadOnlyList<nint>>? PcDismemberNpcCollisionProvider { get; set; }

    /// <summary>Optional sink for the body-side recoil: (sourceAddress, body-side bone, angular
    /// velocity). When the piece is kicked away with the activation impulse, the stump above the cut
    /// is spun so it visibly flicks up instead of being pushed straight into the body.</summary>
    public Action<nint, string, Vector3>? ReactionRecoilSink { get; set; }

    /// <summary>How hard the stump above a cut flicks up — the cut-end recoil speed (m/s). Applied
    /// when a piece is kicked off (needs the activation impulse to fire). 0 disables the recoil.</summary>
    public float ReactionRecoilStrength { get; set; }

    /// <summary>Scales the dropped-head collision hull (1 = default). Tune against the debug overlay.</summary>
    public float HeadShapeScale { get; set; } = 1f;

    /// <summary>Extra downward offset of the visible head vs its collision hull (m); raises HeadShape
    /// drop to sink a floating head onto the ground.</summary>
    public float HeadShapeDrop { get; set; }

    /// <summary>Optional lookup for client-spawned BNpc identity. Native BNpc setup is the stable path
    /// for monster/creature dismember clones; selected world NPCs may not have these ids cached.</summary>
    public Func<nint, (uint BNpcBaseId, uint BNpcNameId)>? EnemyNpcIdentityResolver { get; set; }

    private sealed class Clone
    {
        public nint SourceAddress;
        public string LimbRootBone = "";
        public int ObjectIndex = -1;
        public long CreatedSeq;
        public BattleChara* Chara;
        public IGameObject? GameObjectRef;
        public Vector3 SeveranceWorldPos;
        public Quaternion SeveranceWorldRot;
        public Vector3 OutwardWorldDir;
        public bool IsPlayerControlledSource;
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
        public bool UseMonsterAppearance;
        public int ExpectedModelCharaId;
        public string? GlamourBase64;
        public int GlamourFramesUntil = -1;
        public int GlamourAttemptsLeft;
        public HandoffSnapshot? Handoff;
        public int ResolveFramesWaited; // frames spent (hidden) waiting for the limb bone to load
        public Vector3 SourceScale = Vector3.One; // source's draw scale, applied so big enemies' pieces match
        public int ExpectedSkeletonBoneCount;
        public int ExpectedSkeletonParentCount;
        public int ExpectedSkeletonSignature;

        // Gear-drop mode (hats / accessories): when >= 0 this clone is NOT a severed limb but a single
        // dropped equipment piece. Every model on the clone is hidden EXCEPT this CharacterBase model
        // slot, so only the hat/accessory renders; it is then driven as one rigid body. -1 = limb mode.
        public int GearKeepModelSlot = -1;
        public List<(int Slot, nint Ptr)>? GearHiddenModels; // nulled model pointers, restored on despawn
        public HashSet<int>? GearHiddenSlots;                // slots already cached (avoid dup caching)
    }

    private sealed class HandoffSnapshot
    {
        public Vector3 SkeletonPos;
        public Quaternion SkeletonRot;
        public string RootBone = "";
        public Vector3 RootWorldPos;
        public Quaternion RootWorldRot;
        public readonly Dictionary<string, HandoffBone> Bones = new();
    }

    private struct HandoffBone
    {
        public Vector3 ModelPos;
        public Quaternion ModelRot;
        public Vector3 Scale;
        public Vector3 WorldPos;
        public Quaternion WorldRot;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;
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

    private struct NpcBoneStatic
    {
        public StaticHandle Handle;
        public TypedIndex Shape;
        public int BoneIndex;
        public int ParentBoneIndex;
        public float CenterFactor;
    }

    private struct NpcCollisionState
    {
        public nint Address;
        public List<NpcBoneStatic> BoneStatics;
        public StaticHandle FallbackHandle;
        public TypedIndex FallbackShape;
        public bool IsFallback;
    }

    private sealed class Pending
    {
        public nint SourceAddress;
        public string LimbRootBone = "";
        public float Delay;
        public string? GlamourBase64;
        public HandoffSnapshot? LastSample;
        // Source identity captured at SCHEDULE time (the source is still alive then). Reading it
        // later in TrySpawn (~0.5s after death) races the source's teardown: a dead monster reports
        // ModelCharaId=0 and may no longer resolve, so the clone was built as a default human.
        public bool IdentityCaptured;
        public uint BNpcBaseId;
        public uint BNpcNameId;
        public int SourceModelCharaId;
        // Source's overall draw scale, also captured while alive. A scaled-up monster (the live
        // enemy) loses its size when rebuilt via SetupBNpc/ModelCharaId (which start at scale 1),
        // so the severed piece looked far too small versus the body.
        public Vector3 SourceScale = Vector3.One;
        public int SourceSkeletonBoneCount;
        public int SourceSkeletonParentCount;
        public int SourceSkeletonSignature;
        public int GearKeepModelSlot = -1;
    }

    private readonly List<Clone> clones = new();
    private readonly List<Pending> pending = new();
    private readonly HashSet<int> allocatedIndices = new();

    private static readonly string[] HumanoidSignatureBones =
    {
        "j_kosi", "j_sebo_a", "j_kubi",
        "j_ude_a_l", "j_ude_a_r",
        "j_asi_a_l", "j_asi_a_r",
    };

    private static readonly string[] HumanoidEnemyBones =
    {
        "j_kao",
        "j_ude_a_l", "j_ude_a_r",
        "j_ude_b_l", "j_ude_b_r",
        "j_asi_a_l", "j_asi_a_r",
        "j_asi_b_l", "j_asi_b_r",
    };

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

    public IReadOnlyList<string> SelectRandomEnemyBones(nint sourceAddress, int humanoidCount, float genericPercent)
    {
        var skelN = boneService.TryGetSkeleton(sourceAddress);
        if (skelN == null) return Array.Empty<string>();
        var skel = skelN.Value;

        if (IsHumanoidSkeleton(skel))
        {
            var candidates = new List<string>();
            foreach (var bone in HumanoidEnemyBones)
                if (boneService.ResolveBoneIndex(skel, bone) >= 0)
                    candidates.Add(bone);

            return PickRandom(candidates, Math.Clamp(humanoidCount, 0, candidates.Count));
        }

        var genericCandidates = BuildGenericEnemyCandidates(skel);
        var percent = Math.Clamp(genericPercent, 0f, 100f);
        var count = (int)Math.Round(genericCandidates.Count * (percent / 100f), MidpointRounding.AwayFromZero);
        return PickRandomNonOverlapping(skel, genericCandidates, Math.Clamp(count, 0, genericCandidates.Count));
    }

    public void SyncSelectionFor(nint sourceAddress, IReadOnlyCollection<string> selectedBones, string? glamourBase64)
    {
        if (sourceAddress == nint.Zero) return;

        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bone in selectedBones)
            if (!string.IsNullOrWhiteSpace(bone))
                selected.Add(bone);

        if (selected.Count == 0)
        {
            RemoveFor(sourceAddress);
            return;
        }

        var current = GetTrackedBones(sourceAddress);
        var changed = new HashSet<string>(current, StringComparer.Ordinal);
        changed.SymmetricExceptWith(selected);

        var rebuild = new HashSet<string>(StringComparer.Ordinal);
        if (changed.Count > 0)
        {
            var skelN = boneService.TryGetSkeleton(sourceAddress);
            if (skelN != null)
            {
                var skel = skelN.Value;
                foreach (var selectedBone in selected)
                {
                    if (!current.Contains(selectedBone)) continue;
                    foreach (var changedBone in changed)
                    {
                        if (selectedBone == changedBone) continue;
                        if (IsDescendantBoneName(skel, changedBone, selectedBone))
                        {
                            rebuild.Add(selectedBone);
                            break;
                        }
                    }
                }
            }
        }

        foreach (var bone in current)
            if (!selected.Contains(bone) || rebuild.Contains(bone))
                RemoveOne(sourceAddress, bone);

        foreach (var bone in selected)
            if (!HasTrackedBone(sourceAddress, bone))
                SpawnFor(sourceAddress, bone, 0f, glamourBase64);
    }

    /// <summary>Schedule a severed-limb clone for <paramref name="limbRootBone"/> on the given character,
    /// firing after <paramref name="delay"/> (matches the ragdoll activation delay).</summary>
    public void SpawnFor(nint sourceAddress, string limbRootBone, float delay, string? glamourBase64)
    {
        if (sourceAddress == nint.Zero || string.IsNullOrEmpty(limbRootBone)) return;
        if (clones.Exists(c => c.SourceAddress == sourceAddress && c.LimbRootBone == limbRootBone)) return;
        if (pending.Exists(p => p.SourceAddress == sourceAddress && p.LimbRootBone == limbRootBone)) return;
        var p = new Pending
        {
            SourceAddress = sourceAddress,
            LimbRootBone = limbRootBone,
            Delay = MathF.Max(0f, delay),
            GlamourBase64 = glamourBase64,
        };
        CaptureSourceIdentity(p);
        TryRefreshHandoff(p, 1f / 60f);
        pending.Add(p);
    }

    public void SpawnForImmediate(nint sourceAddress, string limbRootBone, string? glamourBase64)
    {
        if (sourceAddress == nint.Zero || string.IsNullOrEmpty(limbRootBone)) return;
        if (clones.Exists(c => c.SourceAddress == sourceAddress && c.LimbRootBone == limbRootBone)) return;
        if (pending.Exists(p => p.SourceAddress == sourceAddress && p.LimbRootBone == limbRootBone)) return;

        var p = new Pending
        {
            SourceAddress = sourceAddress,
            LimbRootBone = limbRootBone,
            Delay = 0f,
            GlamourBase64 = glamourBase64,
        };
        CaptureSourceIdentity(p);
        TryRefreshHandoff(p, 1f / 60f);
        TrySpawn(p);
    }

    /// <summary>Drop a single equipment piece (hat / accessory) as a falling rigid body. Spawns a clone
    /// of <paramref name="sourceAddress"/> that hides every model except CharacterBase model slot
    /// <paramref name="keepModelSlot"/> (Head=0, Ear=5, Neck=6, Wrist=7, RFinger=8, LFinger=9), freezes
    /// it, and tumbles it from <paramref name="attachBone"/>. The caller is expected to unequip the same
    /// slot on the real body afterward (KO strip), so the piece looks like it fell off. Player-only.</summary>
    public void SpawnGearDrop(nint sourceAddress, string attachBone, int keepModelSlot, string? glamourBase64)
    {
        if (sourceAddress == nint.Zero || string.IsNullOrEmpty(attachBone) || keepModelSlot < 0) return;
        // Dedupe by kept model slot, not bone: hats and earrings share the j_kao attach bone.
        if (clones.Exists(c => c.SourceAddress == sourceAddress && c.GearKeepModelSlot == keepModelSlot)) return;
        if (pending.Exists(p => p.SourceAddress == sourceAddress && p.GearKeepModelSlot == keepModelSlot)) return;

        // Only drop if the source actually RENDERS a model in that slot. This is the rendered model, so
        // it covers Glamourer-only glamours too (a glam hat leaves the real equipment id 0 but still
        // draws a model). No model => nothing to drop.
        var srcDraw = ((GameObject*)sourceAddress)->DrawObject;
        if (srcDraw == null) return;
        var srcCb = (CharacterBase*)srcDraw;
        if (srcCb->Models == null || keepModelSlot >= srcCb->SlotCount || srcCb->Models[keepModelSlot] == null)
        {
            log.Info($"GearDrop: source 0x{sourceAddress:X} renders no model in slot {keepModelSlot}; skipping");
            return;
        }

        var p = new Pending
        {
            SourceAddress = sourceAddress,
            LimbRootBone = attachBone,
            Delay = 0f,
            GlamourBase64 = glamourBase64,
            GearKeepModelSlot = keepModelSlot,
        };
        CaptureSourceIdentity(p);
        TryRefreshHandoff(p, 1f / 60f);
        TrySpawn(p);
    }

    // Snapshot the source's BNpc identity + ModelCharaId while it is still alive (at schedule
    // time). TrySpawn fires ~0.5s later, after the source may have been torn down, when these
    // reads return 0/null and the clone would fall back to a default human model.
    private void CaptureSourceIdentity(Pending p)
    {
        if (p.IdentityCaptured || p.SourceAddress == nint.Zero) return;
        var ids = EnemyNpcIdentityResolver?.Invoke(p.SourceAddress) ?? default;
        p.BNpcBaseId = ids.BNpcBaseId;
        p.BNpcNameId = ids.BNpcNameId;
        var src = (Character*)p.SourceAddress;
        p.SourceModelCharaId = src->ModelContainer.ModelCharaId;
        var srcDraw = ((GameObject*)p.SourceAddress)->DrawObject;
        if (srcDraw != null)
        {
            var s = srcDraw->Scale;
            if (s.X > 0f && s.Y > 0f && s.Z > 0f)
                p.SourceScale = s;
        }
        if (boneService.TryGetSkeleton(p.SourceAddress) is { } skel)
            CaptureSourceSkeletonFingerprint(p, skel);
        p.IdentityCaptured = true;
    }

    private static void CaptureSourceSkeletonFingerprint(Pending p, SkeletonAccess skel)
    {
        p.SourceSkeletonBoneCount = skel.BoneCount;
        p.SourceSkeletonParentCount = skel.ParentCount;
        p.SourceSkeletonSignature = ComputeSkeletonSignature(skel);
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

    private HashSet<string> GetTrackedBones(nint sourceAddress)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in pending)
            if (p.SourceAddress == sourceAddress)
                result.Add(p.LimbRootBone);
        foreach (var c in clones)
            if (c.SourceAddress == sourceAddress)
                result.Add(c.LimbRootBone);
        return result;
    }

    private bool HasTrackedBone(nint sourceAddress, string limbRootBone)
    {
        return pending.Exists(p => p.SourceAddress == sourceAddress && p.LimbRootBone == limbRootBone) ||
               clones.Exists(c => c.SourceAddress == sourceAddress && c.LimbRootBone == limbRootBone);
    }

    private void RemoveOne(nint sourceAddress, string limbRootBone)
    {
        pending.RemoveAll(p => p.SourceAddress == sourceAddress && p.LimbRootBone == limbRootBone);
        for (int i = clones.Count - 1; i >= 0; i--)
        {
            var c = clones[i];
            if (c.SourceAddress != sourceAddress || c.LimbRootBone != limbRootBone) continue;
            DespawnClone(c);
            clones.RemoveAt(i);
        }
    }

    private bool IsDescendantBoneName(SkeletonAccess skel, string boneName, string rootBoneName)
    {
        var bone = boneService.ResolveBoneIndex(skel, boneName);
        var root = boneService.ResolveBoneIndex(skel, rootBoneName);
        return bone >= 0 && root >= 0 && IsDescendantOrSelf(skel, bone, root);
    }

    public void RemoveAll()
    {
        pending.Clear();
        if (clones.Count == 0)
        {
            ClearPcNpcCollisionStatics();
            return;
        }
        foreach (var c in clones) DespawnClone(c);
        clones.Clear();
        ClearPcNpcCollisionStatics();
    }

    private void OnRenderFrame()
    {
        try
        {
            TickCloneSlotCooldowns();

            // Tick pending spawns (delay countdown).
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                const float dt = 1f / 60f;
                TryRefreshHandoff(p, dt);
                p.Delay -= dt;
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

            UpdatePcNpcCollisionStatics();
            if (simulation != null) simulation.Timestep(1f / 60f);

            for (int i = clones.Count - 1; i >= 0; i--)
            {
                var c = clones[i];
                if (!c.DrawEnabled) continue;
                try
                {
                    ApplyDeferredGlamour(c);
                    if (!UpdateClone(c))
                    {
                        DespawnClone(c);
                        clones.RemoveAt(i);
                    }
                }
                catch (Exception ex)
                {
                    log.Warning(ex, $"Dismember: dropping clone idx={c.ObjectIndex} after update failure");
                    DespawnClone(c);
                    clones.RemoveAt(i);
                }
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
        var srcRot = Quaternion.Normalize(new Quaternion(srcSk->Transform.Rotation.X, srcSk->Transform.Rotation.Y, srcSk->Transform.Rotation.Z, srcSk->Transform.Rotation.W));
        ref var lm = ref skel.Pose->ModelPose.Data[limbIdx];
        var limbModelPos = new Vector3(lm.Translation.X, lm.Translation.Y, lm.Translation.Z);
        var limbModelRot = new Quaternion(lm.Rotation.X, lm.Rotation.Y, lm.Rotation.Z, lm.Rotation.W);
        var handoff = p.LastSample;
        var severancePos = handoff?.RootWorldPos ?? (srcPos + Vector3.Transform(limbModelPos, srcRot));
        var severanceRot = handoff?.RootWorldRot ?? Quaternion.Normalize(srcRot * limbModelRot);
        var cloneBasePos = handoff?.SkeletonPos ?? severancePos;
        var outwardDir = ComputeOutwardDirection(skel, limbIdx, severancePos, srcPos, srcRot);
        var player = Core.Services.ObjectTable.LocalPlayer;
        var isPlayerControlledSource = player != null && player.Address == p.SourceAddress;

        var src = (Character*)p.SourceAddress;
        var sourceIsHumanoid = IsHumanoidSkeleton(skel);
        // Use the identity captured at SCHEDULE time (source alive). A live read here races the
        // source's post-death teardown: a torn-down monster reports ModelCharaId=0 and may no longer
        // resolve, so the clone would be built as a default human (the "stretched human limb" bug).
        CaptureSourceIdentity(p); // no-op if already captured; fallback only
        if (p.SourceSkeletonSignature == 0)
            CaptureSourceSkeletonFingerprint(p, skel);
        var sourceModelCharaId = p.SourceModelCharaId;
        var ids = (BNpcBaseId: p.BNpcBaseId, BNpcNameId: p.BNpcNameId);
        // Appearance must be decided from actor/model identity, not just skeleton
        // topology. Some monster models use human-like skeletons; routing those
        // through the humanoid copy path bootstraps them as player-shaped clones.
        var useMonsterAppearance = !isPlayerControlledSource &&
            (ids.BNpcBaseId > 0 || sourceModelCharaId > 0);

        var clientObjMgr = ClientObjectManager.Instance();
        if (clientObjMgr == null) return;
        var createResult = TryCreateCloneActor(clientObjMgr, isPlayerControlledSource, severancePos, out var index);
        if (createResult == 0xFFFFFFFF)
            return;

        allocatedIndices.Add(index);
        var obj = clientObjMgr->GetObjectByIndex((ushort)index);
        if (obj == null) { allocatedIndices.Remove(index); log.Warning("Dismember: object null after create"); return; }

        var chara = (BattleChara*)obj;
        var character = (Character*)chara;

        obj->DisableDraw();
        obj->ObjectKind = useMonsterAppearance ? ObjectKind.BattleNpc : ObjectKind.Pc;
        obj->SubKind = useMonsterAppearance ? (byte)BattleNpcSubKind.Combatant : (byte)0;
        obj->TargetableStatus = 0; // never targetable
        obj->RenderFlags = (VisibilityFlags)0;
        obj->Position = cloneBasePos;
        obj->Rotation = 0f;
        WriteCloneName((GameObject*)obj, index);

        SetupCloneAppearance(character, src, (GameObject*)obj, sourceIsHumanoid,
            useMonsterAppearance, ids, sourceModelCharaId);
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
            CreatedSeq = nextCloneSeq++,
            Chara = chara,
            GameObjectRef = gameObjectRef,
            SeveranceWorldPos = severancePos,
            SeveranceWorldRot = severanceRot,
            OutwardWorldDir = outwardDir,
            IsPlayerControlledSource = isPlayerControlledSource,
            UseMonsterAppearance = useMonsterAppearance || !sourceIsHumanoid,
            ExpectedModelCharaId = sourceModelCharaId,
            GlamourBase64 = p.GlamourBase64,
            Handoff = handoff,
            SourceScale = p.SourceScale,
            ExpectedSkeletonBoneCount = p.SourceSkeletonBoneCount,
            ExpectedSkeletonParentCount = p.SourceSkeletonParentCount,
            ExpectedSkeletonSignature = p.SourceSkeletonSignature,
            GearKeepModelSlot = p.GearKeepModelSlot,
        });
        log.Info($"Dismember: clone idx={index} bone={p.LimbRootBone} at ({severancePos.X:F1},{severancePos.Y:F1},{severancePos.Z:F1})");
    }

    private uint TryCreateCloneActor(ClientObjectManager* mgr, bool isPlayerControlledSource, Vector3 spawnPos, out int index)
    {
        index = -1;
        if (!isPlayerControlledSource)
            TrimNonPlayerClonesForReserve(spawnPos);

        for (var pass = 0; pass < 2; pass++)
        {
            var createResult = TryCreateCloneActorInFreeSlot(mgr, isPlayerControlledSource, out index);
            if (createResult != 0xFFFFFFFF)
                return createResult;

            if (!TryReclaimNonPlayerClone(spawnPos))
                break;
        }

        log.Warning(isPlayerControlledSource
            ? "Dismember: no free clone slot for player piece"
            : "Dismember: no free clone slot for actor piece");
        return 0xFFFFFFFF;
    }

    private uint TryCreateCloneActorInFreeSlot(ClientObjectManager* mgr, bool isPlayerControlledSource, out int index)
    {
        index = -1;
        EnsureCloneSlotQueue();

        var nonPlayerLimit = (int)(CloneSlotEndExclusive - CloneSlotStart) - ReservedPlayerCloneSlots;
        if (!isPlayerControlledSource && CountNonPlayerClones() >= nonPlayerLimit)
            return 0xFFFFFFFF;

        var attempts = freeCloneSlots.Count;
        for (var i = 0; i < attempts; i++)
        {
            var hint = freeCloneSlots.Dequeue();
            if (allocatedIndices.Contains((int)hint)) continue;
            if (coolingCloneSlotSet.Contains((int)hint)) continue;
            if (mgr->GetObjectByIndex((ushort)hint) != null)
            {
                freeCloneSlots.Enqueue(hint);
                continue;
            }

            var createResult = mgr->CreateBattleCharacter(hint);
            if (createResult == 0xFFFFFFFF)
            {
                log.Warning($"Dismember: CreateBattleCharacter failed (hint={hint})");
                CooldownCloneSlot((int)hint);
                continue;
            }

            index = (int)createResult;
            return createResult;
        }

        return 0xFFFFFFFF;
    }

    private void EnsureCloneSlotQueue()
    {
        if (cloneSlotQueueInitialized)
            return;

        for (uint hint = CloneSlotStart; hint < CloneSlotEndExclusive; hint++)
            freeCloneSlots.Enqueue(hint);
        cloneSlotQueueInitialized = true;
    }

    private void CooldownCloneSlot(int slot)
    {
        if (slot < CloneSlotStart || slot >= CloneSlotEndExclusive)
            return;
        if (!coolingCloneSlotSet.Add(slot))
            return;
        coolingCloneSlots.Enqueue(((uint)slot, CloneSlotReuseCooldownFrames));
    }

    private void TickCloneSlotCooldowns()
    {
        if (coolingCloneSlots.Count == 0)
            return;

        EnsureCloneSlotQueue();
        var count = coolingCloneSlots.Count;
        for (var i = 0; i < count; i++)
        {
            var item = coolingCloneSlots.Dequeue();
            item.Frames--;
            if (item.Frames > 0)
            {
                coolingCloneSlots.Enqueue(item);
                continue;
            }

            coolingCloneSlotSet.Remove((int)item.Slot);
            if (!allocatedIndices.Contains((int)item.Slot))
                freeCloneSlots.Enqueue(item.Slot);
        }
    }

    private int CountNonPlayerClones()
    {
        var count = 0;
        foreach (var c in clones)
            if (!c.IsPlayerControlledSource)
                count++;
        return count;
    }

    private void TrimNonPlayerClonesForReserve(Vector3 spawnPos)
    {
        var nonPlayerLimit = (int)(CloneSlotEndExclusive - CloneSlotStart) - ReservedPlayerCloneSlots;
        while (CountNonPlayerClones() >= nonPlayerLimit)
        {
            if (!TryReclaimNonPlayerClone(spawnPos))
                return;
        }
    }

    private bool TryReclaimNonPlayerClone(Vector3 spawnPos)
    {
        var victimIndex = -1;
        long oldestSeq = long.MaxValue;
        var farthestDistSq = -1f;

        for (int i = 0; i < clones.Count; i++)
        {
            var c = clones[i];
            if (c.IsPlayerControlledSource)
                continue;

            var distSq = Vector3.DistanceSquared(c.SeveranceWorldPos, spawnPos);
            if (c.CreatedSeq < oldestSeq ||
                (c.CreatedSeq == oldestSeq && distSq > farthestDistSq))
            {
                oldestSeq = c.CreatedSeq;
                farthestDistSq = distSq;
                victimIndex = i;
            }
        }

        if (victimIndex < 0)
            return false;

        var victim = clones[victimIndex];
        log.Info($"Dismember: reclaiming clone idx={victim.ObjectIndex}");
        DespawnClone(victim);
        clones.RemoveAt(victimIndex);
        return true;
    }

    private void SetupCloneAppearance(Character* target, Character* source, GameObject* obj,
        bool sourceIsHumanoid, bool useMonsterAppearance,
        (uint BNpcBaseId, uint BNpcNameId) ids, int sourceModelCharaId)
    {
        if (useMonsterAppearance || !sourceIsHumanoid)
        {
            obj->ObjectKind = ObjectKind.BattleNpc;
            obj->SubKind = (byte)BattleNpcSubKind.Combatant;

            if (ids.BNpcBaseId > 0)
            {
                target->CharacterSetup.SetupBNpc(ids.BNpcBaseId, ids.BNpcNameId);
                log.Info($"Dismember: monster clone SetupBNpc({ids.BNpcBaseId}, {ids.BNpcNameId})");
            }
            else
            {
                target->ModelContainer.ModelCharaId = sourceModelCharaId;
                log.Info($"Dismember: monster clone ModelCharaId={target->ModelContainer.ModelCharaId}");
            }

            target->CharacterSetup.CopyFromCharacter(target, CharacterSetupContainer.CopyFlags.None);
            obj->ObjectKind = ObjectKind.BattleNpc;
            obj->SubKind = (byte)BattleNpcSubKind.Combatant;
        }
        else
        {
            var sourceObj = (GameObject*)source;
            var flags = sourceObj->ObjectKind == ObjectKind.BattleNpc
                ? CharacterSetupContainer.CopyFlags.None
                : CharacterSetupContainer.CopyFlags.ClassJob | CharacterSetupContainer.CopyFlags.WeaponHiding;
            target->CharacterSetup.CopyFromCharacter(source, flags);
            target->CharacterSetup.CopyFromCharacter(target, CharacterSetupContainer.CopyFlags.None);
            obj->ObjectKind = ObjectKind.Pc;
            obj->SubKind = 0;
        }

        obj->TargetableStatus = 0;
        WriteCloneName((GameObject*)obj, (int)obj->ObjectIndex);
    }

    private static Vector3 ComputeOutwardDirection(
        SkeletonAccess skel,
        int limbIdx,
        Vector3 severanceWorldPos,
        Vector3 sourceWorldPos,
        Quaternion sourceWorldRot)
    {
        var parentIdx = -1;
        if (limbIdx >= 0 && limbIdx < skel.ParentCount)
            parentIdx = skel.HavokSkeleton->ParentIndices[limbIdx];

        if (parentIdx >= 0 && parentIdx < skel.BoneCount)
        {
            ref var pm = ref skel.Pose->ModelPose.Data[parentIdx];
            var parentModelPos = new Vector3(pm.Translation.X, pm.Translation.Y, pm.Translation.Z);
            var parentWorldPos = sourceWorldPos + Vector3.Transform(parentModelPos, sourceWorldRot);
            var dir = severanceWorldPos - parentWorldPos;
            if (dir.LengthSquared() > 1e-6f)
                return Vector3.Normalize(dir);
        }

        var fallback = severanceWorldPos - sourceWorldPos;
        return fallback.LengthSquared() > 1e-6f ? Vector3.Normalize(fallback) : Vector3.UnitX;
    }

    private void TryRefreshHandoff(Pending p, float dt)
    {
        var sample = CaptureHandoff(p.SourceAddress, p.LimbRootBone, p.LastSample, dt);
        if (sample != null)
            p.LastSample = sample;
    }

    private HandoffSnapshot? CaptureHandoff(nint sourceAddress, string rootBone, HandoffSnapshot? previous, float dt)
    {
        var skelN = boneService.TryGetSkeleton(sourceAddress);
        if (skelN == null) return null;
        var skel = skelN.Value;
        var rootIdx = boneService.ResolveBoneIndex(skel, rootBone);
        if (rootIdx < 0) return null;

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

        ref var rootPose = ref skel.Pose->ModelPose.Data[rootIdx];
        var rootModelPos = new Vector3(rootPose.Translation.X, rootPose.Translation.Y, rootPose.Translation.Z);
        var rootModelRot = Quaternion.Normalize(new Quaternion(rootPose.Rotation.X, rootPose.Rotation.Y, rootPose.Rotation.Z, rootPose.Rotation.W));

        var result = new HandoffSnapshot
        {
            SkeletonPos = skelPos,
            SkeletonRot = skelRot,
            RootBone = rootBone,
            RootWorldPos = skelPos + Vector3.Transform(rootModelPos, skelRot),
            RootWorldRot = Quaternion.Normalize(skelRot * rootModelRot),
        };

        var n = Math.Min(skel.BoneCount, skel.ParentCount);
        var maxSpan = 0f;
        var maxScale = 0f;
        for (int i = 0; i < n; i++)
        {
            if (!IsDescendantOrSelf(skel, i, rootIdx)) continue;
            var name = skel.HavokSkeleton->Bones[i].Name.String;
            if (string.IsNullOrEmpty(name)) continue;

            ref var m = ref skel.Pose->ModelPose.Data[i];
            var modelPos = new Vector3(m.Translation.X, m.Translation.Y, m.Translation.Z);
            var modelRot = Quaternion.Normalize(new Quaternion(m.Rotation.X, m.Rotation.Y, m.Rotation.Z, m.Rotation.W));
            var scale = new Vector3(m.Scale.X, m.Scale.Y, m.Scale.Z);
            var worldPos = skelPos + Vector3.Transform(modelPos, skelRot);
            var worldRot = Quaternion.Normalize(skelRot * modelRot);

            var bone = new HandoffBone
            {
                ModelPos = modelPos,
                ModelRot = modelRot,
                Scale = scale,
                WorldPos = worldPos,
                WorldRot = worldRot,
            };

            if (previous != null && previous.Bones.TryGetValue(name, out var prev) && dt > 1e-5f)
            {
                bone.LinearVelocity = ClampVectorLength((worldPos - prev.WorldPos) / dt, 8f);
                bone.AngularVelocity = ClampVectorLength(AngularVelocityFromQuats(prev.WorldRot, worldRot, dt), 30f);
            }

            result.Bones[name] = bone;
            maxSpan = MathF.Max(maxSpan, (modelPos - rootModelPos).Length());
            maxScale = MathF.Max(maxScale, MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)));
        }

        // Once the source-side limb has been hidden, all descendant bones collapse to the
        // same tiny point. Keep the last real sample instead of replacing it with that pose.
        if (!IsHeadLimb(rootBone) && (maxSpan < 0.035f || maxScale < 0.05f))
            return null;

        return result.Bones.Count > 0 ? result : null;
    }

    private void TryEnableDraw(Clone c)
    {
        c.FramesWaited++;
        if (c.Chara == null) return;
        if (c.UseMonsterAppearance && !IsMonsterCloneReady(c))
        {
            if (c.FramesWaited == MaxPendingFrames)
                log.Warning($"Dismember: monster clone idx={c.ObjectIndex} not ready; keeping hidden");
            return;
        }
        if (!c.Chara->IsReadyToDraw() && c.FramesWaited < MaxPendingFrames) return;

        c.Chara->EnableDraw();
        c.DrawEnabled = true;
        c.SettleFrames = 8;        // let it idle into a real pose before freezing
        c.GlamourFramesUntil = 1;  // apply WHILE the clone is still alive (frozen actors may not redraw)
        c.GlamourAttemptsLeft = 20;
        if (IsHeadLimb(c.LimbRootBone) && c.GearKeepModelSlot < 0)
        {
            c.KeepTimelineRunning = true;
            animationController.PlayDeathAnimationOnActor((Character*)c.Chara, forceCombatDeath: true);
        }
        log.Info($"Dismember: clone idx={c.ObjectIndex} drawn (settling)");
    }

    private static bool IsMonsterCloneReady(Clone c)
    {
        var obj = (GameObject*)c.Chara;
        if (obj->ObjectKind != ObjectKind.BattleNpc)
            return false;

        var character = (Character*)c.Chara;
        var modelCharaId = character->ModelContainer.ModelCharaId;
        if (c.ExpectedModelCharaId > 0)
            return modelCharaId == c.ExpectedModelCharaId;
        return modelCharaId > 0;
    }

    private void ApplyHandoffPose(SkeletonAccess skel, Clone c)
    {
        var handoff = c.Handoff;
        if (handoff == null) return;

        SetCloneBaseTransform(c, handoff.SkeletonPos, handoff.SkeletonRot);
        foreach (var (name, bone) in handoff.Bones)
        {
            var idx = boneService.ResolveBoneIndex(skel, name);
            if (idx < 0 || idx >= skel.BoneCount) continue;
            ref var m = ref skel.Pose->ModelPose.Data[idx];
            m.Translation.X = bone.ModelPos.X;
            m.Translation.Y = bone.ModelPos.Y;
            m.Translation.Z = bone.ModelPos.Z;
            m.Rotation.X = bone.ModelRot.X;
            m.Rotation.Y = bone.ModelRot.Y;
            m.Rotation.Z = bone.ModelRot.Z;
            m.Rotation.W = bone.ModelRot.W;
            m.Scale.X = bone.Scale.X;
            m.Scale.Y = bone.Scale.Y;
            m.Scale.Z = bone.Scale.Z;
        }
    }

    private void SetCloneBaseTransform(Clone c, Vector3 pos, Quaternion rot)
    {
        var obj = (GameObject*)c.Chara;
        obj->Position = pos;
        var drawObj = obj->DrawObject;
        if (drawObj == null) return;

        drawObj->Position = pos;
        drawObj->Rotation = rot;
        ApplyCloneDrawScale(c, drawObj);
        var cb = (CharacterBase*)drawObj;
        var sk = cb->Skeleton;
        if (sk == null) return;
        sk->Transform.Position.X = pos.X;
        sk->Transform.Position.Y = pos.Y;
        sk->Transform.Position.Z = pos.Z;
        sk->Transform.Rotation.X = rot.X;
        sk->Transform.Rotation.Y = rot.Y;
        sk->Transform.Rotation.Z = rot.Z;
        sk->Transform.Rotation.W = rot.W;
    }

    private void SeedBodyVelocity(BodyHandle handle, Clone c, string boneName)
    {
        if (simulation == null || c.Handoff == null) return;
        if (!c.Handoff.Bones.TryGetValue(boneName, out var bone)) return;
        var body = simulation.Bodies.GetBodyReference(handle);
        body.Velocity.Linear = bone.LinearVelocity;
        body.Velocity.Angular = bone.AngularVelocity;
    }

    private void ApplyActivationImpulse(Clone c)
    {
        if (simulation == null) return;

        var impulse = MathF.Max(0f, DismemberActivationImpulse);
        if (impulse <= 0f) return;

        var dir = c.OutwardWorldDir;
        if (dir.LengthSquared() < 1e-6f)
            dir = Vector3.UnitX;
        else
            dir = Vector3.Normalize(dir);

        var velocityDelta = dir * impulse;

        if (c.Rig != null)
        {
            foreach (var limbBody in c.Rig.Bodies)
                ApplyVelocityDelta(limbBody.Body, velocityDelta);
        }
        else if (c.Body.HasValue)
        {
            ApplyVelocityDelta(c.Body.Value, velocityDelta);
        }

        // Body-side recoil. The piece flies out roughly ALONG the bone, so a lever-arm torque
        // (seg × outwardImpulse) is ~zero (force parallel to the lever) and a straight reverse push
        // just gets soaked up by the rest of the ragdoll — both invisible. Instead drive an explicit
        // up-swing of the stump: rotate it so the cut end lifts (perpendicular to the bone), at a
        // speed proportional to the impulse.
        if (ReactionRecoilSink != null && ReactionRecoilStrength > 0f)
        {
            var parentBone = ResolveBodyParentBone(c.SourceAddress, c.LimbRootBone);
            if (!string.IsNullOrEmpty(parentBone))
            {
                var cutPos = boneService.GetBoneWorldPos(c.SourceAddress, c.LimbRootBone);
                var stumpPos = boneService.GetBoneWorldPos(c.SourceAddress, parentBone!);
                if (cutPos.HasValue && stumpPos.HasValue)
                {
                    var seg = cutPos.Value - stumpPos.Value;       // stump pivot -> cut
                    var segLen = seg.Length();
                    if (segLen > 1e-3f)
                    {
                        var segDir = seg / segLen;
                        // "Up" component perpendicular to the bone — the direction to lift the cut end.
                        var liftDir = Vector3.UnitY - Vector3.Dot(Vector3.UnitY, segDir) * segDir;
                        if (liftDir.LengthSquared() < 1e-4f) liftDir = Vector3.Cross(segDir, Vector3.UnitX);
                        if (liftDir.LengthSquared() < 1e-4f) liftDir = Vector3.Cross(segDir, Vector3.UnitZ);
                        liftDir = Vector3.Normalize(liftDir);

                        var tipSpeed = ReactionRecoilStrength;               // cut-end recoil speed (m/s)
                        // omega such that (omega × seg) == liftDir × tipSpeed (lifts the cut end up).
                        var angularVel = Vector3.Cross(segDir, liftDir) * (tipSpeed / segLen);
                        ReactionRecoilSink(c.SourceAddress, parentBone!, angularVel);
                    }
                }
            }
        }
    }

    /// <summary>Bone immediately above <paramref name="boneName"/> in the source skeleton — the
    /// body-side "stump" bone that should recoil at the cut.</summary>
    private string? ResolveBodyParentBone(nint sourceAddress, string boneName)
    {
        var skelN = boneService.TryGetSkeleton(sourceAddress);
        if (skelN == null) return null;
        var skel = skelN.Value;
        var idx = boneService.ResolveBoneIndex(skel, boneName);
        if (idx < 0) return null;
        var parentIdx = skel.HavokSkeleton->ParentIndices[idx];
        if (parentIdx < 0 || parentIdx >= skel.BoneCount) return null;
        return skel.HavokSkeleton->Bones[parentIdx].Name.String;
    }

    private void ApplyVelocityDelta(BodyHandle handle, Vector3 velocityDelta)
    {
        if (simulation == null) return;
        var body = simulation.Bodies.GetBodyReference(handle);
        body.Velocity.Linear += velocityDelta;
        body.Awake = true;
    }

    private void RegisterPcCollisionBodies(Clone c)
    {
        if (!c.IsPlayerControlledSource)
            return;

        if (c.Rig != null)
        {
            foreach (var limbBody in c.Rig.Bodies)
                pcCollisionBodyHandles.Add(limbBody.Body.Value);
            return;
        }

        if (c.Body.HasValue)
            pcCollisionBodyHandles.Add(c.Body.Value.Value);
    }

    private void UnregisterPcCollisionBodies(Clone c)
    {
        if (!c.IsPlayerControlledSource)
            return;

        if (c.Rig != null)
        {
            foreach (var limbBody in c.Rig.Bodies)
                pcCollisionBodyHandles.Remove(limbBody.Body.Value);
        }

        if (c.Body.HasValue)
            pcCollisionBodyHandles.Remove(c.Body.Value.Value);
    }

    private const float PcNpcCollisionDefaultRadius = 0.045f;
    private const float PcNpcCollisionFallbackRadius = 0.32f;
    private const float PcNpcCollisionFallbackLength = 1.2f;
    private const float PcNpcCollisionMinSegmentLength = 0.05f;
    private const int PcNpcCollisionMaxSegments = 24;

    private void UpdatePcNpcCollisionStatics()
    {
        if (!EnablePcDismemberNpcCollision || simulation == null || pcCollisionBodyHandles.Count == 0)
        {
            ClearPcNpcCollisionStatics();
            return;
        }

        var addresses = PcDismemberNpcCollisionProvider?.Invoke() ?? Array.Empty<nint>();
        var nextAddresses = new HashSet<nint>();
        var playerAddress = Core.Services.ObjectTable.LocalPlayer?.Address ?? nint.Zero;
        foreach (var address in addresses)
        {
            if (address == nint.Zero || address == playerAddress)
                continue;
            nextAddresses.Add(address);
        }

        if (nextAddresses.Count == 0)
        {
            ClearPcNpcCollisionStatics();
            return;
        }

        if (!pcNpcCollisionAddresses.SetEquals(nextAddresses))
        {
            ClearPcNpcCollisionStatics();
            foreach (var address in nextAddresses)
                BuildPcNpcCollision(address);
            pcNpcCollisionAddresses.UnionWith(nextAddresses);
        }

        for (int i = pcNpcCollisionStates.Count - 1; i >= 0; i--)
            UpdatePcNpcCollisionState(pcNpcCollisionStates[i]);

        WakePcCollisionBodies();
    }

    private void BuildPcNpcCollision(nint address)
    {
        if (simulation == null)
            return;

        var skelN = boneService.TryGetSkeleton(address);
        if (skelN == null || skelN.Value.CharBase->Skeleton == null)
        {
            CreatePcNpcFallbackCollision(address);
            return;
        }

        var skel = skelN.Value;
        var skeleton = skel.CharBase->Skeleton;
        var skelPos = new Vector3(skeleton->Transform.Position.X, skeleton->Transform.Position.Y, skeleton->Transform.Position.Z);
        var skelRot = Quaternion.Normalize(new Quaternion(
            skeleton->Transform.Rotation.X,
            skeleton->Transform.Rotation.Y,
            skeleton->Transform.Rotation.Z,
            skeleton->Transform.Rotation.W));

        var candidates = new List<(float Len, int Bone, int Parent, float CenterFactor, Vector3 Center, Quaternion Rot)>();
        var boneCount = Math.Min(skel.BoneCount, skel.ParentCount);
        for (int i = 1; i < boneCount; i++)
        {
            var parentIdx = skel.HavokSkeleton->ParentIndices[i];
            if (parentIdx < 0 || parentIdx >= skel.BoneCount)
                continue;

            ref var parentMt = ref skel.Pose->ModelPose.Data[parentIdx];
            var parentWorld = skelPos + Vector3.Transform(
                new Vector3(parentMt.Translation.X, parentMt.Translation.Y, parentMt.Translation.Z),
                skelRot);

            ref var childMt = ref skel.Pose->ModelPose.Data[i];
            var childWorld = skelPos + Vector3.Transform(
                new Vector3(childMt.Translation.X, childMt.Translation.Y, childMt.Translation.Z),
                skelRot);

            var segment = childWorld - parentWorld;
            var segLen = segment.Length();
            if (segLen < PcNpcCollisionMinSegmentLength)
                continue;

            var centerFactor = 0.5f;
            var center = parentWorld + segment * centerFactor;
            candidates.Add((segLen, i, parentIdx, centerFactor, center, AlignYTo(segment / segLen)));
        }

        if (candidates.Count > PcNpcCollisionMaxSegments)
        {
            candidates.Sort((a, b) => b.Len.CompareTo(a.Len));
            candidates.RemoveRange(PcNpcCollisionMaxSegments, candidates.Count - PcNpcCollisionMaxSegments);
        }

        if (candidates.Count == 0)
        {
            CreatePcNpcFallbackCollision(address);
            return;
        }

        var statics = new List<NpcBoneStatic>();
        foreach (var c in candidates)
        {
            var shape = simulation.Shapes.Add(new Capsule(PcNpcCollisionDefaultRadius, MathF.Max(0.02f, c.Len - PcNpcCollisionDefaultRadius * 2f)));
            var handle = simulation.Statics.Add(new StaticDescription(c.Center, c.Rot, shape));
            restrictedStaticHandles.Add(handle.Value);
            statics.Add(new NpcBoneStatic
            {
                Handle = handle,
                Shape = shape,
                BoneIndex = c.Bone,
                ParentBoneIndex = c.Parent,
                CenterFactor = c.CenterFactor,
            });
        }

        pcNpcCollisionStates.Add(new NpcCollisionState
        {
            Address = address,
            BoneStatics = statics,
        });
    }

    private void CreatePcNpcFallbackCollision(nint address)
    {
        if (simulation == null)
            return;

        var go = (GameObject*)address;
        var pos = new Vector3(go->Position.X, go->Position.Y + 0.8f, go->Position.Z);
        var shape = simulation.Shapes.Add(new Capsule(PcNpcCollisionFallbackRadius, PcNpcCollisionFallbackLength));
        var handle = simulation.Statics.Add(new StaticDescription(pos, Quaternion.Identity, shape));
        restrictedStaticHandles.Add(handle.Value);
        pcNpcCollisionStates.Add(new NpcCollisionState
        {
            Address = address,
            BoneStatics = new List<NpcBoneStatic>(),
            FallbackHandle = handle,
            FallbackShape = shape,
            IsFallback = true,
        });
    }

    private void UpdatePcNpcCollisionState(NpcCollisionState state)
    {
        if (simulation == null)
            return;

        try
        {
            if (state.IsFallback)
            {
                var go = (GameObject*)state.Address;
                var staticRef = simulation.Statics.GetStaticReference(state.FallbackHandle);
                staticRef.Pose.Position = new Vector3(go->Position.X, go->Position.Y + 0.8f, go->Position.Z);
                staticRef.UpdateBounds();
                return;
            }

            var skelN = boneService.TryGetSkeleton(state.Address);
            if (skelN == null || skelN.Value.CharBase->Skeleton == null)
                return;

            var skel = skelN.Value;
            var skeleton = skel.CharBase->Skeleton;
            var skelPos = new Vector3(skeleton->Transform.Position.X, skeleton->Transform.Position.Y, skeleton->Transform.Position.Z);
            var skelRot = Quaternion.Normalize(new Quaternion(
                skeleton->Transform.Rotation.X,
                skeleton->Transform.Rotation.Y,
                skeleton->Transform.Rotation.Z,
                skeleton->Transform.Rotation.W));

            foreach (var bs in state.BoneStatics)
            {
                if (bs.BoneIndex < 0 || bs.BoneIndex >= skel.BoneCount ||
                    bs.ParentBoneIndex < 0 || bs.ParentBoneIndex >= skel.BoneCount)
                    continue;

                ref var parentMt = ref skel.Pose->ModelPose.Data[bs.ParentBoneIndex];
                var parentWorld = skelPos + Vector3.Transform(
                    new Vector3(parentMt.Translation.X, parentMt.Translation.Y, parentMt.Translation.Z),
                    skelRot);

                ref var childMt = ref skel.Pose->ModelPose.Data[bs.BoneIndex];
                var childWorld = skelPos + Vector3.Transform(
                    new Vector3(childMt.Translation.X, childMt.Translation.Y, childMt.Translation.Z),
                    skelRot);

                var segment = childWorld - parentWorld;
                var segLen = segment.Length();
                var staticRef = simulation.Statics.GetStaticReference(bs.Handle);
                if (segLen > 0.01f)
                {
                    staticRef.Pose.Position = parentWorld + segment * bs.CenterFactor;
                    staticRef.Pose.Orientation = AlignYTo(segment / segLen);
                }
                else
                {
                    staticRef.Pose.Position = parentWorld;
                    staticRef.Pose.Orientation = Quaternion.Identity;
                }
                staticRef.UpdateBounds();
            }
        }
        catch { }
    }

    private void ClearPcNpcCollisionStatics()
    {
        if (simulation != null)
        {
            foreach (var state in pcNpcCollisionStates)
            {
                if (state.IsFallback)
                {
                    try { simulation.Statics.Remove(state.FallbackHandle); } catch { }
                    if (bufferPool != null)
                        try { simulation.Shapes.RemoveAndDispose(state.FallbackShape, bufferPool); } catch { }
                    continue;
                }

                foreach (var bs in state.BoneStatics)
                {
                    try { simulation.Statics.Remove(bs.Handle); } catch { }
                    if (bufferPool != null)
                        try { simulation.Shapes.RemoveAndDispose(bs.Shape, bufferPool); } catch { }
                }
            }
        }

        pcNpcCollisionStates.Clear();
        pcNpcCollisionAddresses.Clear();
        restrictedStaticHandles.Clear();
    }

    private void WakePcCollisionBodies()
    {
        if (simulation == null)
            return;

        foreach (var handleValue in pcCollisionBodyHandles)
        {
            try
            {
                var body = simulation.Bodies.GetBodyReference(new BodyHandle(handleValue));
                body.Awake = true;
            }
            catch { }
        }
    }

    private bool UpdateClone(Clone c)
    {
        if (c.Chara == null) return false;
        var drawObj = ((GameObject*)c.Chara)->DrawObject;
        if (drawObj == null) return !c.Armed;
        ApplyCloneDrawScale(c, drawObj);

        var skelN = boneService.TryGetSkeleton((nint)c.Chara);
        if (skelN == null) return !c.Armed;
        var skel = skelN.Value;

        if (c.GearKeepModelSlot >= 0)
            return UpdateGearClone(c, drawObj, skel);

        if (c.UseMonsterAppearance && !IsExpectedCloneSkeleton(c, skel))
        {
            // ModelContainer.ModelCharaId can update before the DrawObject/Skeleton has actually
            // swapped away from the default humanoid. If we resolve a shared bone name during that
            // window (e.g. j_ude_b_l), the clone arms as a scaled human limb. Keep it hidden until
            // the real source skeleton signature is present.
            HideEntireBody(c);
            if (++c.ResolveFramesWaited >= MaxResolveFrames)
            {
                log.Warning($"Dismember: clone idx={c.ObjectIndex} skeleton did not match source (got bones={skel.BoneCount}, parents={skel.ParentCount}); dropping");
                return false;
            }
            return true;
        }

        if (c.LimbIndex < 0)
            c.LimbIndex = boneService.ResolveBoneIndex(skel, c.LimbRootBone);
        if (c.LimbIndex < 0)
        {
            // The limb bone isn't on the skeleton yet — the model/skeleton is still loading, or a
            // placeholder/wrong model got built (the cause of the rare "phantom": an un-collapsed
            // full body near the corpse, common at battle start / for big uncached monster models).
            // Park the whole clone far away until the bone resolves so no full figure is ever shown;
            // this touches only the root transform (restored by ApplyHandoffPose), never per-bone
            // scale, so the limb later appears at full size. Drop the clone if it never resolves.
            HideEntireBody(c);
            if (++c.ResolveFramesWaited >= MaxResolveFrames)
            {
                log.Warning($"Dismember: clone idx={c.ObjectIndex} bone '{c.LimbRootBone}' never resolved on clone skeleton; dropping");
                return false;
            }
            return true;
        }
        if (!IsCloneSkeletonCompatible(c, skel))
            return false;

        var boundaryRoots = GetSelectedChildRoots(skel, c.SourceAddress, c.LimbIndex, c.LimbRootBone);

        if (!c.Armed)
            ApplyHandoffPose(skel, c);

        // Each frame: show ONLY the limb (others thrown far away) and hide the clone's weapons.
        HideAllButLimb(skel, c.LimbIndex, boundaryRoots);
        HideWeapons(c);

        if (!c.Armed)
        {
            // Let the pose settle (the limb gets a real shape), then freeze + spawn the body.
            if (--c.SettleFrames > 0) return true;
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
                    if (!IsSupportedPieceBone(skel, i, c.LimbIndex, boundaryRoots)) continue;
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
                    c.Rig = BuildHeadRig(skel, c);
                    if (c.Rig == null)
                    {
                        c.Body = simulation.Bodies.Add(BodyDescription.CreateDynamic(
                            new RigidPose(c.SeveranceWorldPos, c.SeveranceWorldRot),
                            default(BodyVelocity),
                            limbInertia,
                            new CollidableDescription(limbShapeIndex, 0.04f),
                            new BodyActivityDescription(0.01f)));
                        SeedBodyVelocity(c.Body.Value, c, c.LimbRootBone);
                    }
                }
                else
                {
                    c.Rig = BuildLimbRig(skel, c, boundaryRoots);
                    if (c.Rig == null)
                    {
                        var shape = BuildLimbShape(skel, c, out var inertia);
                        c.Body = simulation.Bodies.Add(BodyDescription.CreateDynamic(
                            new RigidPose(c.SeveranceWorldPos, c.SeveranceWorldRot),
                            default(BodyVelocity),
                            inertia,
                            new CollidableDescription(shape, 0.04f),
                            new BodyActivityDescription(0.01f)));
                        SeedBodyVelocity(c.Body.Value, c, c.LimbRootBone);
                    }
                }
                (c.GroundTile, c.GroundShape) = CreateTerrainPatch(c.SeveranceWorldPos.X, c.SeveranceWorldPos.Z, c.SeveranceWorldPos.Y);
                RegisterPcCollisionBodies(c);
                ApplyActivationImpulse(c);
            }
            c.Armed = true;
            log.Info($"Dismember: clone idx={c.ObjectIndex} armed (limbIdx={c.LimbIndex})");
            return true;
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
        if (simulation == null) return true;
        if (c.Rig != null)
        {
            DriveLimbRig(skel, c);
            HideAllButLimb(skel, c.LimbIndex, boundaryRoots);
            HideWeapons(c);
            return true;
        }
        if (c.Body == null) return true;
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
        return true;
    }

    // Gear-drop update: a single equipment piece (hat / accessory) falling off. Unlike a limb, the piece
    // is NOT isolated by collapsing bones (a hat shares the head bone with the face & hair); instead we
    // hide every CharacterBase model except the dropped slot, freeze the clone, and drive its whole
    // skeleton from one rigid body so the attach bone — and the gear skinned to it — tumbles.
    private bool UpdateGearClone(Clone c, DrawObject* drawObj, SkeletonAccess skel)
    {
        if (c.LimbIndex < 0)
            c.LimbIndex = boneService.ResolveBoneIndex(skel, c.LimbRootBone);
        if (c.LimbIndex < 0)
        {
            HideEntireBody(c);
            if (++c.ResolveFramesWaited >= MaxResolveFrames)
            {
                log.Warning($"GearDrop: clone idx={c.ObjectIndex} attach bone '{c.LimbRootBone}' never resolved; dropping");
                return false;
            }
            return true;
        }

        // Hide face / hair / body / other gear so ONLY the dropped piece renders. Re-asserted each frame
        // (models load a few frames after EnableDraw, and a glamour redraw can repopulate them).
        HideNonKeptModels(c);
        HideWeapons(c);

        if (!c.Armed)
        {
            // Keep the clone parked off-screen until the kept gear model is actually loaded, so a full
            // un-hidden body never flashes next to the player.
            if (!IsKeptModelPresent(c))
            {
                HideEntireBody(c);
                if (++c.ResolveFramesWaited >= MaxResolveFrames)
                {
                    log.Warning($"GearDrop: clone idx={c.ObjectIndex} gear model slot {c.GearKeepModelSlot} never loaded; dropping");
                    return false;
                }
                return true;
            }

            ApplyHandoffPose(skel, c);
            if (--c.SettleFrames > 0) return true;

            ref var lm = ref skel.Pose->ModelPose.Data[c.LimbIndex];
            c.LimbRootModelPos = new Vector3(lm.Translation.X, lm.Translation.Y, lm.Translation.Z);
            ((Character*)c.Chara)->Timeline.OverallSpeed = 0f;

            EnsureSimulation();
            if (simulation != null)
            {
                c.Body = simulation.Bodies.Add(BodyDescription.CreateDynamic(
                    new RigidPose(c.SeveranceWorldPos, c.SeveranceWorldRot),
                    default(BodyVelocity),
                    limbInertia,
                    new CollidableDescription(limbShapeIndex, 0.04f),
                    new BodyActivityDescription(0.01f)));
                SeedBodyVelocity(c.Body.Value, c, c.LimbRootBone);
                (c.GroundTile, c.GroundShape) = CreateTerrainPatch(c.SeveranceWorldPos.X, c.SeveranceWorldPos.Z, c.SeveranceWorldPos.Y);
                RegisterPcCollisionBodies(c);
                ApplyActivationImpulse(c);
            }
            c.Armed = true;
            log.Info($"GearDrop: clone idx={c.ObjectIndex} armed (slot={c.GearKeepModelSlot}, bone={c.LimbRootBone})");
            return true;
        }

        // Armed: keep animation frozen and drive the whole clone skeleton from the rigid body so the
        // attach bone sits at the body and the gear tumbles about it (limb single-body idiom).
        ((Character*)c.Chara)->Timeline.OverallSpeed = 0f;
        if (simulation == null || c.Body == null) return true;
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
        return true;
    }

    // Null every CharacterBase model pointer except the dropped gear slot, so only the hat/accessory
    // renders. The render loop skips null slots. Original pointers are cached and restored on despawn so
    // the game's destructor still frees those models (a null slot would otherwise leak them).
    private void HideNonKeptModels(Clone c)
    {
        if (c.Chara == null) return;
        var drawObj = ((GameObject*)c.Chara)->DrawObject;
        if (drawObj == null) return;
        var cb = (CharacterBase*)drawObj;
        if (cb->Models == null) return;
        var slots = cb->SlotCount;
        c.GearHiddenModels ??= new List<(int, nint)>();
        c.GearHiddenSlots ??= new HashSet<int>();
        for (int i = 0; i < slots; i++)
        {
            if (i == c.GearKeepModelSlot) continue;
            var m = cb->Models[i];
            if (m == null) continue;
            if (c.GearHiddenSlots.Add(i))
                c.GearHiddenModels.Add((i, (nint)m));
            cb->Models[i] = null;
        }
    }

    private bool IsKeptModelPresent(Clone c)
    {
        if (c.Chara == null) return false;
        var drawObj = ((GameObject*)c.Chara)->DrawObject;
        if (drawObj == null) return false;
        var cb = (CharacterBase*)drawObj;
        if (cb->Models == null) return false;
        return c.GearKeepModelSlot >= 0 && c.GearKeepModelSlot < cb->SlotCount
            && cb->Models[c.GearKeepModelSlot] != null;
    }

    private void RestoreHiddenModels(Clone c)
    {
        if (c.GearHiddenModels == null || c.Chara == null) return;
        var drawObj = ((GameObject*)c.Chara)->DrawObject;
        if (drawObj == null) return;
        var cb = (CharacterBase*)drawObj;
        if (cb->Models == null) return;
        foreach (var (slot, ptr) in c.GearHiddenModels)
            if (slot >= 0 && slot < cb->SlotCount && cb->Models[slot] == null)
                cb->Models[slot] = (RenderModel*)ptr;
    }

    private static void ApplyCloneDrawScale(Clone c, DrawObject* drawObj)
    {
        if (drawObj == null) return;
        var s = c.SourceScale;
        if (s.X <= 0f || s.Y <= 0f || s.Z <= 0f) return;
        if (MathF.Abs(s.X - 1f) < 0.0001f &&
            MathF.Abs(s.Y - 1f) < 0.0001f &&
            MathF.Abs(s.Z - 1f) < 0.0001f)
            return;

        drawObj->Scale = s;
        drawObj->NotifyTransformChanged();
    }

    private static bool IsExpectedCloneSkeleton(Clone c, SkeletonAccess skel)
    {
        if (c.ExpectedSkeletonSignature == 0)
            return true;
        if (skel.BoneCount != c.ExpectedSkeletonBoneCount ||
            skel.ParentCount != c.ExpectedSkeletonParentCount)
            return false;
        return ComputeSkeletonSignature(skel) == c.ExpectedSkeletonSignature;
    }

    private static int ComputeSkeletonSignature(SkeletonAccess skel)
    {
        unchecked
        {
            var hash = 17;
            var n = Math.Min(skel.BoneCount, skel.ParentCount);
            hash = hash * 31 + skel.BoneCount;
            hash = hash * 31 + skel.ParentCount;
            for (int i = 0; i < n; i++)
            {
                hash = hash * 31 + skel.HavokSkeleton->ParentIndices[i];
                var name = skel.HavokSkeleton->Bones[i].Name.String;
                hash = hash * 31 + (name?.GetHashCode(StringComparison.Ordinal) ?? 0);
            }
            return hash == 0 ? 1 : hash;
        }
    }

    private bool IsCloneSkeletonCompatible(Clone c, SkeletonAccess skel)
    {
        var n = Math.Min(skel.BoneCount, skel.ParentCount);
        if (!IsValidBoneIndex(c.LimbIndex, n))
        {
            log.Warning($"Dismember: clone idx={c.ObjectIndex} limb index {c.LimbIndex} invalid after skeleton change; dropping");
            return false;
        }

        if (c.LimbSnapshot != null)
        {
            foreach (var s in c.LimbSnapshot)
            {
                if (!IsValidBoneIndex(s.Idx, n))
                {
                    log.Warning($"Dismember: clone idx={c.ObjectIndex} snapshot index {s.Idx} invalid after skeleton change; dropping");
                    return false;
                }
            }
        }

        if (c.Rig != null)
        {
            foreach (var rb in c.Rig.Bodies)
            {
                if (!IsValidBoneIndex(rb.BoneIndex, n))
                {
                    log.Warning($"Dismember: clone idx={c.ObjectIndex} rig index {rb.BoneIndex} invalid after skeleton change; dropping");
                    return false;
                }
            }

            foreach (var idx in c.Rig.BoneIndices)
            {
                if (!IsValidBoneIndex(idx, n))
                {
                    log.Warning($"Dismember: clone idx={c.ObjectIndex} rig set index {idx} invalid after skeleton change; dropping");
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsValidBoneIndex(int index, int count)
        => index >= 0 && index < count;

    private LimbRig? BuildLimbRig(SkeletonAccess skel, Clone c, IReadOnlyList<int> boundaryRoots)
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
        foreach (var def in defs)
        {
            if (!IsLimbRigRole(def.AnatomicalRole) || def.SoftBody)
                continue;
            var idx = boneService.ResolveBoneIndex(skel, def.Name);
            if (idx < 0 || !IsPhysicsPieceBone(skel, idx, c.LimbIndex, boundaryRoots))
                continue;
            selected.Add(def);
        }

        if (selected.Count < 1)
        {
            selected = BuildGenericPieceDefs(skel, c.LimbIndex, boundaryRoots);
            defs = selected.ToArray();
        }

        if (selected.Count < 1)
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
            else if (TryFindChildWorldPos(skel, skelPos, skelRot, def.Name, defs, c.LimbIndex, out childWorldPos))
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
            SeedBodyVelocity(handle, c, def.Name);

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

    private LimbRig? BuildHeadRig(SkeletonAccess skel, Clone c)
    {
        if (simulation == null || c.LimbIndex < 0) return null;
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

        ref var mt = ref skel.Pose->ModelPose.Data[c.LimbIndex];
        var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
        var modelRot = Quaternion.Normalize(new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W));
        var boneWorldPos = skelPos + Vector3.Transform(modelPos, skelRot);
        var boneWorldRot = Quaternion.Normalize(skelRot * modelRot);

        var def = ResolveHeadDef();
        // Head shape: a faceted convex hull roughly following the head's contour — flatter face,
        // fuller back of skull, brow/cheek/jaw, a chin and a nose bump. Unlike a sphere it has flat
        // regions, so a rolling head settles on the cheek/back instead of rolling forever; unlike the
        // old box it still tumbles naturally and isn't oversized (no floating). ~2 dozen points, cheap.
        var s = MathF.Max(0.2f, HeadShapeScale);
        var hullPoints = BuildHeadHullPoints(0.072f * s, 0.090f * s, 0.082f * s);
        var hull = new ConvexHull((Span<Vector3>)hullPoints, bufferPool!, out var hullCenter);
        var shape = simulation.Shapes.Add(hull);
        var inertia = hull.ComputeInertia(MathF.Max(1f, def.Mass));

        // Hull is in the head-bone frame (origin = bone). Place the body at its centroid; the bone
        // reconstructs at centroid - up*centerOffset, so HeadShapeDrop sinks the visible head if the
        // collision rests higher than the mesh (kills floating).
        var centerOffset = hullCenter.Y + HeadShapeDrop;
        var center = boneWorldPos + Vector3.Transform(hullCenter, boneWorldRot);
        var handle = simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(center, boneWorldRot),
            default(BodyVelocity),
            inertia,
            new CollidableDescription(shape, 0.04f),
            new BodyActivityDescription(0.01f)));
        SeedBodyVelocity(handle, c, c.LimbRootBone);

        var rig = new LimbRig();
        rig.Shapes.Add(shape);
        rig.Bodies.Add(new LimbBody
        {
            BoneIndex = c.LimbIndex,
            Name = c.LimbRootBone,
            Body = handle,
            BodyToBoneRotation = Quaternion.Identity,
            SegmentHalfLength = centerOffset,
            Def = def,
        });
        rig.BoneIndices.Add(c.LimbIndex);
        log.Info($"Dismember: head rig idx={c.ObjectIndex}");
        return rig;
    }

    // Head contour as a smooth egg in the head-bone frame (origin = head bone ~ skull center, +Y up
    // to crown, +Z forward/face, +X right). Rings on an ellipsoid: rounded crown, tapered chin (so it
    // rolls then settles like an egg), face side (+Z) slightly flattened, plus a nose bump. Enough
    // points to roll; the taper/face/nose give it places to settle.
    private static Vector3[] BuildHeadHullPoints(float rx, float ry, float rz)
    {
        const int rings = 5, seg = 12;
        var p = new List<Vector3>(rings * seg + 4);
        for (int r = 1; r < rings; r++)
        {
            float t = r / (float)rings;                  // 0..1 bottom->top
            float y = -ry + 2f * ry * t;                 // chin .. crown
            float prof = MathF.Sin(MathF.PI * t);        // 0 at poles, 1 at middle
            float taper = 0.6f + 0.4f * t;               // narrower toward the chin
            float ring = prof * taper;
            for (int s = 0; s < seg; s++)
            {
                float a = (s / (float)seg) * MathF.Tau;
                float x = MathF.Cos(a) * rx * ring;
                float z = MathF.Sin(a) * rz * ring;
                if (z > 0f) z *= 0.82f;                   // flatten the face
                p.Add(new Vector3(x, y, z));
            }
        }
        p.Add(new Vector3(0f, ry, -0.05f * rz));          // crown (slightly back)
        p.Add(new Vector3(0f, -ry, 0.10f * rz));          // chin tip (slightly forward)
        p.Add(new Vector3(0f, -0.15f * ry, rz * 1.02f));  // nose bump
        return p.ToArray();
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

        if (c.KeepTimelineRunning && IsHeadLimb(c.LimbRootBone))
            boneService.PropagateToPartialSkeletons(skel, c.LimbIndex, "j_kao", result);

        for (int i = 0; i < skel.BoneCount && i < skel.ParentCount; i++)
        {
            if (c.Rig.BoneIndices.Contains(i) || !IsDescendantOrSelf(skel, i, c.LimbIndex))
                continue;

            var parentIdx = skel.HavokSkeleton->ParentIndices[i];
            if (parentIdx < 0 || parentIdx >= skel.BoneCount || !result.HasAccumulated[parentIdx])
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

    private bool IsHumanoidSkeleton(SkeletonAccess skel)
    {
        foreach (var bone in HumanoidSignatureBones)
            if (boneService.ResolveBoneIndex(skel, bone) < 0)
                return false;
        return true;
    }

    private List<string> BuildGenericEnemyCandidates(SkeletonAccess skel)
    {
        var result = new List<string>();
        var n = Math.Min(skel.BoneCount, skel.ParentCount);
        if (n < 2) return result;

        var positions = ReadModelPositions(skel, n);
        var children = BuildChildren(skel, n);
        var (distToParent, longestChild, maxSegment) = MeasureSkeletonSegments(skel, positions, children, n);
        var minSegment = Math.Clamp(maxSegment * 0.06f, 0.02f, 0.08f);
        var subtreeCounts = ComputeSubtreeCounts(children);

        for (var i = 1; i < n; i++)
        {
            var parent = skel.HavokSkeleton->ParentIndices[i];
            if (parent < 0 || parent >= n) continue;
            if (subtreeCounts[i] <= 1) continue;
            if (subtreeCounts[i] > n * 0.55f) continue;
            if (distToParent[i] < minSegment && longestChild[i] < minSegment) continue;
            if (children[parent].Count == 1 && subtreeCounts[i] > n * 0.35f) continue;

            var name = BoneName(skel, i);
            if (!string.IsNullOrEmpty(name))
                result.Add(name);
        }

        result.Sort((a, b) =>
        {
            var ai = boneService.ResolveBoneIndex(skel, a);
            var bi = boneService.ResolveBoneIndex(skel, b);
            var ac = ai >= 0 ? subtreeCounts[ai] : 0;
            var bc = bi >= 0 ? subtreeCounts[bi] : 0;
            return bc.CompareTo(ac);
        });
        return result;
    }

    private List<RagdollController.RagdollBoneDef> BuildGenericPieceDefs(
        SkeletonAccess skel,
        int limbRoot,
        IReadOnlyList<int> boundaryRoots)
    {
        var n = Math.Min(skel.BoneCount, skel.ParentCount);
        var result = new List<RagdollController.RagdollBoneDef>();
        if (limbRoot < 0 || limbRoot >= n) return result;

        var positions = ReadModelPositions(skel, n);
        var children = BuildChildren(skel, n);
        var (distToParent, longestChild, maxSegment) = MeasureSkeletonSegments(skel, positions, children, n);
        var minSegment = Math.Clamp(maxSegment * 0.06f, 0.02f, 0.08f);

        var selected = new bool[n];
        for (var i = 0; i < n; i++)
        {
            if (!IsPhysicsPieceBone(skel, i, limbRoot, boundaryRoots))
                continue;
            if (i == limbRoot || longestChild[i] >= minSegment || distToParent[i] >= minSegment)
                selected[i] = true;
        }

        string? SelectedAncestorName(int i)
        {
            var p = skel.HavokSkeleton->ParentIndices[i];
            while (p >= 0 && p < n)
            {
                if (selected[p]) return BoneName(skel, p);
                if (p == limbRoot) break;
                p = skel.HavokSkeleton->ParentIndices[p];
            }
            return null;
        }

        for (var i = 0; i < n; i++)
        {
            if (!selected[i]) continue;
            var name = BoneName(skel, i);
            if (string.IsNullOrEmpty(name)) continue;

            var segLen = MathF.Max(minSegment, MathF.Max(longestChild[i], distToParent[i]));
            var radius = Math.Clamp(segLen * 0.28f, 0.02f, 0.18f);
            result.Add(new RagdollController.RagdollBoneDef
            {
                Name = name,
                ParentName = i == limbRoot ? null : SelectedAncestorName(i),
                CapsuleRadius = radius,
                CapsuleHalfLength = segLen * 0.45f,
                Mass = Math.Clamp(segLen * 20f, 0.5f, 12f),
                SwingLimit = 0.6f,
                Joint = RagdollController.JointType.Ball,
                TwistMinAngle = -0.35f,
                TwistMaxAngle = 0.35f,
                AnatomicalRole = RagdollController.AnatomicalRole.Generic,
                ColliderShape = RagdollController.RagdollColliderShape.Capsule,
                BoxHalfExtents = new Vector3(radius, segLen * 0.45f, radius),
            });
        }

        return result;
    }

    private static List<string> PickRandom(List<string> candidates, int count)
    {
        for (var i = candidates.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        if (count >= candidates.Count) return candidates;
        return candidates.GetRange(0, count);
    }

    private List<string> PickRandomNonOverlapping(SkeletonAccess skel, List<string> candidates, int count)
    {
        var shuffled = PickRandom(candidates, candidates.Count);
        var result = new List<string>();
        foreach (var bone in shuffled)
        {
            var idx = boneService.ResolveBoneIndex(skel, bone);
            if (idx < 0) continue;

            var overlaps = false;
            foreach (var existing in result)
            {
                var existingIdx = boneService.ResolveBoneIndex(skel, existing);
                if (existingIdx < 0) continue;
                if (IsDescendantOrSelf(skel, idx, existingIdx) || IsDescendantOrSelf(skel, existingIdx, idx))
                {
                    overlaps = true;
                    break;
                }
            }

            if (overlaps) continue;
            result.Add(bone);
            if (result.Count >= count) break;
        }
        return result;
    }

    private static Vector3[] ReadModelPositions(SkeletonAccess skel, int count)
    {
        var result = new Vector3[count];
        for (var i = 0; i < count; i++)
        {
            ref var mt = ref skel.Pose->ModelPose.Data[i];
            result[i] = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
        }
        return result;
    }

    private static List<int>[] BuildChildren(SkeletonAccess skel, int count)
    {
        var children = new List<int>[count];
        for (var i = 0; i < count; i++)
            children[i] = new List<int>();

        for (var i = 1; i < count; i++)
        {
            var parent = skel.HavokSkeleton->ParentIndices[i];
            if (parent >= 0 && parent < count)
                children[parent].Add(i);
        }
        return children;
    }

    private static (float[] DistToParent, float[] LongestChild, float MaxSegment) MeasureSkeletonSegments(
        SkeletonAccess skel,
        Vector3[] positions,
        List<int>[] children,
        int count)
    {
        var distToParent = new float[count];
        var longestChild = new float[count];
        var maxSegment = 0f;

        for (var i = 1; i < count; i++)
        {
            var parent = skel.HavokSkeleton->ParentIndices[i];
            if (parent < 0 || parent >= count) continue;
            var dist = Vector3.Distance(positions[i], positions[parent]);
            distToParent[i] = dist;
            if (dist > maxSegment) maxSegment = dist;
        }

        for (var i = 0; i < count; i++)
            foreach (var child in children[i])
                if (distToParent[child] > longestChild[i])
                    longestChild[i] = distToParent[child];

        return (distToParent, longestChild, maxSegment);
    }

    private static int[] ComputeSubtreeCounts(List<int>[] children)
    {
        var counts = new int[children.Length];
        int Count(int i)
        {
            var total = 1;
            foreach (var child in children[i])
                total += Count(child);
            counts[i] = total;
            return total;
        }
        Count(0);
        return counts;
    }

    private static string BoneName(SkeletonAccess skel, int index)
    {
        if (index < 0 || index >= skel.BoneCount)
            return string.Empty;
        return skel.HavokSkeleton->Bones[index].Name.String ?? string.Empty;
    }

    private RagdollController.RagdollBoneDef ResolveHeadDef()
    {
        foreach (var def in GetRagdollBoneDefs())
            if (string.Equals(def.Name, "j_kao", StringComparison.Ordinal))
                return def;

        foreach (var def in RagdollController.DefaultBoneDefs)
            if (string.Equals(def.Name, "j_kao", StringComparison.Ordinal))
                return def;

        return new RagdollController.RagdollBoneDef
        {
            Name = "j_kao",
            CapsuleRadius = 0.08f,
            CapsuleHalfLength = 0.04f,
            Mass = 3.5f,
            AnatomicalRole = RagdollController.AnatomicalRole.Head,
        };
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

    private bool TryFindChildWorldPos(
        SkeletonAccess skel,
        Vector3 skelPos,
        Quaternion skelRot,
        string parentName,
        RagdollController.RagdollBoneDef[] defs,
        int limbRoot,
        out Vector3 worldPos)
    {
        foreach (var def in defs)
        {
            if (!string.Equals(def.ParentName, parentName, StringComparison.Ordinal))
                continue;
            var idx = boneService.ResolveBoneIndex(skel, def.Name);
            if (idx < 0 || !IsDescendantOrSelf(skel, idx, limbRoot))
                continue;
            ref var mt = ref skel.Pose->ModelPose.Data[idx];
            var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            worldPos = skelPos + Vector3.Transform(modelPos, skelRot);
            return true;
        }

        worldPos = default;
        return false;
    }

    private TypedIndex CreateRigShape(RagdollController.RagdollBoneDef def, float bodyHalfLength, float mass, out BodyInertia inertia)
    {
        if (def.ColliderShape == RagdollController.RagdollColliderShape.Box)
        {
            var extents = ResolveBoxHalfExtents(def, bodyHalfLength) * RigVolumeScale(def.AnatomicalRole);
            var box = new Box(extents.X * 2f, extents.Y * 2f, extents.Z * 2f);
            inertia = box.ComputeInertia(mass);
            return simulation!.Shapes.Add(box);
        }

        var radius = MathF.Max(MinRigRadius(def.AnatomicalRole), def.CapsuleRadius * RigVolumeScale(def.AnatomicalRole));
        var capsule = new Capsule(radius, bodyHalfLength * 2f * 1.1f);
        inertia = capsule.ComputeInertia(mass);
        return simulation!.Shapes.Add(capsule);
    }

    private static float RigVolumeScale(RagdollController.AnatomicalRole role)
        => role is RagdollController.AnatomicalRole.Shoulder
            or RagdollController.AnatomicalRole.Elbow
            or RagdollController.AnatomicalRole.Hand
            ? 1.55f
            : 1.35f;

    private static float MinRigRadius(RagdollController.AnatomicalRole role)
        => role is RagdollController.AnatomicalRole.Shoulder
            or RagdollController.AnatomicalRole.Elbow
            or RagdollController.AnatomicalRole.Hand
            ? 0.038f
            : 0.045f;

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

    // Collapse every bone outside this clone's visible piece. If a child piece is also selected,
    // keep that child root as the parent's distal cut support and collapse only its descendants.
    private void HideAllButLimb(SkeletonAccess skel, int limbIdx, IReadOnlyList<int> boundaryRoots)
    {
        var pose = skel.Pose;
        ref var lm = ref pose->ModelPose.Data[limbIdx];
        var cx = lm.Translation.X;
        var cy = lm.Translation.Y;
        var cz = lm.Translation.Z;

        var n = Math.Min(skel.BoneCount, skel.ParentCount);
        for (int i = 0; i < n; i++)
        {
            if (IsSupportedPieceBone(skel, i, limbIdx, boundaryRoots)) continue;
            var collapseX = cx;
            var collapseY = cy;
            var collapseZ = cz;
            var boundaryRoot = FindContainingStrictRoot(skel, i, boundaryRoots);
            if (boundaryRoot >= 0)
            {
                ref var bm = ref pose->ModelPose.Data[boundaryRoot];
                collapseX = bm.Translation.X;
                collapseY = bm.Translation.Y;
                collapseZ = bm.Translation.Z;
            }
            ref var m = ref pose->ModelPose.Data[i];
            m.Translation.X = collapseX; m.Translation.Y = collapseY; m.Translation.Z = collapseZ;
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
            if (mainIdx >= 0 && IsSupportedPieceBone(skel, mainIdx, limbIdx, boundaryRoots)) continue;
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

    // Park the whole clone far below until its limb bone resolves, so no full body ever shows
    // near the corpse. This moves ONLY the skeleton root transform — it does NOT touch any
    // per-bone pose or scale, so nothing is corrupted. The instant the limb resolves,
    // ApplyHandoffPose restores the transform and HideAllButLimb reveals just the severed limb
    // at its true size (zeroing per-bone scale here used to leak into the armed snapshot and
    // shrink the limb).
    private static readonly Vector3 ParkBelow = new(0f, -10000f, 0f);
    private void HideEntireBody(Clone c)
    {
        var obj = (GameObject*)c.Chara;
        obj->Position = ParkBelow;
        var drawObj = obj->DrawObject;
        if (drawObj == null) return;
        drawObj->Position = ParkBelow;
        var sk = ((CharacterBase*)drawObj)->Skeleton;
        if (sk == null) return;
        sk->Transform.Position.X = ParkBelow.X;
        sk->Transform.Position.Y = ParkBelow.Y;
        sk->Transform.Position.Z = ParkBelow.Z;
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

    private List<int> GetSelectedChildRoots(SkeletonAccess skel, nint sourceAddress, int limbIdx, string limbRootBone)
    {
        var result = new List<int>();
        foreach (var selectedBone in GetTrackedBones(sourceAddress))
        {
            if (string.Equals(selectedBone, limbRootBone, StringComparison.Ordinal))
                continue;
            var selectedIdx = boneService.ResolveBoneIndex(skel, selectedBone);
            if (selectedIdx < 0 || !IsDescendantOrSelf(skel, selectedIdx, limbIdx))
                continue;

            var coveredByExisting = false;
            for (int i = 0; i < result.Count; i++)
            {
                if (IsDescendantOrSelf(skel, selectedIdx, result[i]))
                {
                    coveredByExisting = true;
                    break;
                }

                if (IsDescendantOrSelf(skel, result[i], selectedIdx))
                {
                    result[i] = selectedIdx;
                    coveredByExisting = true;
                    break;
                }
            }

            if (!coveredByExisting)
                result.Add(selectedIdx);
        }
        return result;
    }

    private static bool IsSupportedPieceBone(SkeletonAccess skel, int bone, int limbRoot, IReadOnlyList<int> boundaryRoots)
    {
        if (!IsDescendantOrSelf(skel, bone, limbRoot))
            return false;
        return FindContainingStrictRoot(skel, bone, boundaryRoots) < 0;
    }

    private static bool IsPhysicsPieceBone(SkeletonAccess skel, int bone, int limbRoot, IReadOnlyList<int> boundaryRoots)
    {
        if (!IsSupportedPieceBone(skel, bone, limbRoot, boundaryRoots))
            return false;
        foreach (var root in boundaryRoots)
            if (bone == root)
                return false;
        return true;
    }

    private static int FindContainingStrictRoot(SkeletonAccess skel, int bone, IReadOnlyList<int> roots)
    {
        foreach (var root in roots)
            if (bone != root && IsDescendantOrSelf(skel, bone, root))
                return root;
        return -1;
    }

    private void DespawnClone(Clone c)
    {
        try
        {
            // Put the nulled model pointers back so the game's CharacterBase destructor frees them.
            if (c.GearHiddenModels != null) RestoreHiddenModels(c);

            if (simulation != null)
            {
                UnregisterPcCollisionBodies(c);
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
        CooldownCloneSlot(c.ObjectIndex);
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

    private static Vector3 AngularVelocityFromQuats(Quaternion prev, Quaternion curr, float dt)
    {
        if (dt <= 1e-5f) return Vector3.Zero;
        var dq = Quaternion.Normalize(curr * Quaternion.Conjugate(prev));
        if (dq.W < 0f) dq = new Quaternion(-dq.X, -dq.Y, -dq.Z, -dq.W);
        var s = MathF.Sqrt(MathF.Max(0f, 1f - dq.W * dq.W));
        if (s < 1e-5f) return Vector3.Zero;
        var angle = 2f * MathF.Acos(Math.Clamp(dq.W, -1f, 1f));
        var axis = new Vector3(dq.X, dq.Y, dq.Z) / s;
        return axis * (angle / dt);
    }

    private static Vector3 ClampVectorLength(Vector3 v, float maxLen)
    {
        var lenSq = v.LengthSquared();
        if (lenSq > maxLen * maxLen && lenSq > 1e-8f)
            return v * (maxLen / MathF.Sqrt(lenSq));
        return v;
    }

    private void EnsureSimulation()
    {
        if (simulation != null) return;
        bufferPool = new BufferPool();
        simulation = BepuSimulation.Create(
            bufferPool,
            new RagdollNarrowPhaseCallbacks
            {
                ConnectedPairs = connectedPairs,
                Friction = config.RagdollFriction,
                RestrictedStatics = restrictedStaticHandles,
                AllowedDynamicBodiesForRestrictedStatics = pcCollisionBodyHandles,
            },
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
        pcCollisionBodyHandles.Clear();
        pcNpcCollisionStates.Clear();
        pcNpcCollisionAddresses.Clear();
        restrictedStaticHandles.Clear();
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
