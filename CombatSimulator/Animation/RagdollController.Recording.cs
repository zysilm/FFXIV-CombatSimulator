// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using CombatSimulator.Animation.Hair;
using CombatSimulator.Animation.MeshVolume;
using CombatSimulator.Animation.Hosting;
using CombatSimulator.Animation.Recording;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using HkaPose = FFXIVClientStructs.Havok.Animation.Rig.hkaPose;
using HkQsTransform = FFXIVClientStructs.Havok.Common.Base.Math.QsTransform.hkQsTransformf;

namespace CombatSimulator.Animation;

// Read-only recorder for one ragdoll activation (docs/REFACTOR_PLAN.md, stage A0).
//
// Everything here only observes. `rec` is null unless a recording session was on when the ragdoll
// was activated, so each call site in the controller costs one null check. The helpers below read
// the same game memory the surrounding code reads at that moment, never throw into the caller, and
// write records in the order things happen. The format is described in
// AdvancedRagdoll/Recording/RagdollRecordingFormat.cs.
public unsafe partial class RagdollController
{
    private static int recNextControllerId;
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> RecFieldCache = new();

    private readonly int recControllerId = System.Threading.Interlocked.Increment(ref recNextControllerId);
    private RagdollRecordingWriter? rec;
    private RagdollRecordingSession.RecordingFile? recFile;
    // 0 = a call from the rest of the plugin; more = made while a frame or another recorded call runs.
    private int recDepth;
    private int recRenderFrame;
    private int recPhysicsFrame;
    private int recTick;
    private int recFitOutcome;
    private bool recFaultLogged;
    // One watch per entry of RecConfigNames. The bound getters are shared by every controller (built with
    // the first recording); each recorded activation gets its own copies, which remember what it last saw.
    private static RagdollRecordingWatch?[]? recConfigWatchPrototypes;
    private RagdollRecordingWatch?[] recConfigWatches = Array.Empty<RagdollRecordingWatch?>();
    // Created with the first recording, so a controller that is never recorded carries nothing.
    private Dictionary<(ulong Actor, int Partial), (nint Skeleton, int Bones)> recTopologies = null!;
    private Dictionary<(ulong Actor, int Purpose, int Partial), byte[]> recLastPose = null!;
    private Dictionary<object, int> recMeshCaches = null!;
    private int[] recHandles = Array.Empty<int>();
    private byte[] recFlags = Array.Empty<byte>();
    private float[] recStates = Array.Empty<float>();

    /// <summary>Every configuration property the ragdoll code reads (RagdollController*.cs), by name.</summary>
    private static readonly string[] RecConfigNames =
    {
        nameof(Configuration.DismemberPocBones),
        nameof(Configuration.EnableCombatCompanions),
        nameof(Configuration.EnableNpcDeathRagdoll),
        nameof(Configuration.ExtendTerrainDetection),
        nameof(Configuration.GuidedCollapse),
        nameof(Configuration.KoStripPhysicsDropClothing),
        nameof(Configuration.NpcCollisionActive),
        nameof(Configuration.NpcRagdollFineDetail),
        nameof(Configuration.PartyCompanionDeathRagdoll),
        nameof(Configuration.RagdollActivationDelay),
        nameof(Configuration.RagdollAnatomicalRom),
        nameof(Configuration.RagdollExactGroundCollision),
        nameof(Configuration.RagdollHairBodyMesh),
        nameof(Configuration.RagdollHairPhysics),
        nameof(Configuration.RagdollMeshCacheRevision),
        nameof(Configuration.RagdollMeshCollisionLod),
        nameof(Configuration.RagdollMeshContactQuality),
        nameof(Configuration.RagdollMeshLodToleranceMm),
        nameof(Configuration.RagdollMeshVolumes),
        nameof(Configuration.RagdollNpcCollisionAutoSize),
        nameof(Configuration.RagdollNpcCollisionMode),
        nameof(Configuration.RagdollNpcCollisionScale),
        nameof(Configuration.RagdollSoftTissueBonePrefixes),
        nameof(Configuration.RagdollSoftTissueCollision),
        nameof(Configuration.RagdollSoftTissueModBones),
        nameof(Configuration.RagdollSoftTissueScope),
        nameof(Configuration.RagdollSquashIntensity),
        nameof(Configuration.RagdollSquashStretch),
        nameof(Configuration.RagdollTriggerMode),
        nameof(Configuration.RagdollVerboseLog),
        nameof(Configuration.RagdollWaterBuoyancy),
        nameof(Configuration.RagdollWaterLiquid),
        nameof(Configuration.WeaponBodyMeshContacts),
        nameof(Configuration.WeaponDropMeshCollision),
    };

    private static readonly PropertyInfo?[] RecConfigProperties = Array.ConvertAll(RecConfigNames,
        name => typeof(RagdollSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance));

    // ------------------------------------------------------------------ lifetime

    /// <summary>Called from Activate once the target is known. Opens this activation's file when a session is on.</summary>
    private void RecBeginActivation(float delay, RagdollTriggerMode mode, float stagger)
    {
        if (!RagdollRecordingSession.Enabled) return;
        try
        {
            var file = RagdollRecordingSession.TryOpen(targetIsLocalPlayer, targetEntityId);
            if (file == null) return;

            recRenderFrame = recPhysicsFrame = recTick = 0;
            recFitOutcome = 0;
            recFaultLogged = false;
            recDepth = 0;
            recTopologies = new();
            recLastPose = new();
            recMeshCaches = new(ReferenceEqualityComparer.Instance);

            // From here on the file is this controller's: a failure below still closes it at deactivation.
            var w = file.Writer;
            recFile = file;
            rec = w;
            using (w.Begin(RecordType.Header))
            {
                w.I32(RagdollRecordingFormat.Version);
                w.Str(typeof(RagdollController).Assembly.GetName().Version?.ToString() ?? "?");
#if DEV_EXPERIMENTAL
                w.Bool(true);
#else
                w.Bool(false);
#endif
#if PUBLIC_BUILD
                w.Bool(true);
#else
                w.Bool(false);
#endif
                w.Str(RagdollRecordingBepu.BepuVersion());
                w.Str(RuntimeInformation.FrameworkDescription);
                w.Str(RuntimeInformation.OSDescription);
                w.Str(RuntimeInformation.ProcessArchitecture.ToString());
                w.I32(Environment.ProcessorCount);
                w.I32(Vector<float>.Count);
                w.Str($"Vector.IsHardwareAccelerated={Vector.IsHardwareAccelerated} " +
                      $"Sse42={System.Runtime.Intrinsics.X86.Sse42.IsSupported} " +
                      $"Avx={System.Runtime.Intrinsics.X86.Avx.IsSupported} " +
                      $"Avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported} " +
                      $"Fma={System.Runtime.Intrinsics.X86.Fma.IsSupported} " +
                      $"Avx512F={System.Runtime.Intrinsics.X86.Avx512F.IsSupported}");
                w.Bool(BitConverter.IsLittleEndian);
                w.I64(DateTime.UtcNow.Ticks);
                w.I64(System.Diagnostics.Stopwatch.Frequency);
                w.Str(RagdollRecordingSession.SessionId);
                w.I32(file.Index);
                w.I32(recControllerId);
                w.Bool(RunAnimatedHair);
                w.U64((ulong)targetCharacterAddress);
                w.U32(targetEntityId);
                w.I32(actors.GetObjectIndex(targetCharacterAddress));
                w.Bool(targetIsLocalPlayer);
            }

            RecConfigSnapshot(w);
            using (w.Begin(RecordType.Activate))
            {
                w.U64((ulong)targetCharacterAddress);
                w.F32(delay);
                w.I32((int)mode);
                w.F32(stagger);
                w.V3(savedCharacterPosition);
                w.U32(targetEntityId);
                w.I32(actors.GetObjectIndex(targetCharacterAddress));
                w.Bool(targetIsLocalPlayer);
                w.Bool(colliders.HasSelector);
                w.Bool(colliders.HasMovementBlock);
                w.Bool(colliders.HasExtraColliders);
                w.Bool(RunAnimatedHair);
                w.Bool(GrabRigid);
                w.Bool(CarryExternalRigOnAnchor != null);
                // State that calls made before this activation may have left behind.
                w.Bool(pendingDirectedCollapseSpike);
                w.Str(pendingDirectedProfileId);
                w.F32(pendingDirectedDuration);
                w.F32(pendingDirectedFootForce);
                w.F32(pendingDirectedPelvisForce);
                w.F32(pendingDirectedDrop);
                w.F32(pendingDirectedForward);
                w.F32(pendingDirectedPitchDegrees);
                w.Bool(pendingEntryConditionedKneePowerLoss);
                w.Bool(pendingKneePowerLossPattern);
                w.Bool(useLocalDismemberBones);
                w.List(localDismemberBones.Count);
                foreach (var bone in localDismemberBones) w.Str(bone);
                w.List(lightweightNpcCollisionAddresses.Count);
                foreach (var address in lightweightNpcCollisionAddresses) w.U64((ulong)address);
                // State this controller carries over from its earlier activations: neither Activate nor the
                // teardown resets it, so a replay that starts here has to be given it.
                w.U32(impactRandomState);
                w.I32(nextGrabSlotId);
                w.I32(externalBodyGeneration);
                w.List(struckThisWindow.Count);
                foreach (var handle in struckThisWindow) w.I32(handle);
                w.List(strikeColliderAddresses.Count);
                foreach (var address in strikeColliderAddresses) w.U64((ulong)address);
                lock (npcTraversalProxySync)
                {
                    w.I32(npcTraversalProxyRequests.Count);
                    foreach (var entry in npcTraversalProxyRequests)
                    {
                        var request = entry.Value;
                        w.U64((ulong)entry.Key);
                        w.Bool(request.Enabled);
                        w.V3(request.InitialRoot);
                        w.V3(request.TargetRoot);
                        w.F32(request.FloorY);
                        w.F32(request.VisualScale);
                        w.F32(request.MoveSpeed);
                        w.F32(request.MaxClimb);
                        w.F32(request.StompResponse);
                        w.I32(request.ConcurrentStompers);
                        w.Bool(request.Locomoting);
                    }
                }
                // Format version 3: whether the previous activation's last frame wrote a follow position
                // (public builds never reset it).
                w.Bool(followWasActive);
            }
            // Format version 3: the body surfaces this controller carries over from earlier activations (the main
            // controller captures the local player's long before it dies), with the bookkeeping that decides
            // whether they are captured again.
            using (w.Begin(RecordType.MeshProduct))
            {
                w.Str(MeshProductKind.Carried);
                w.U64((ulong)frozenMeshActor);
                w.U32(frozenMeshEntity);
                w.Bool(observedMeshEnabled);
                w.I32(observedMeshCacheRevision);
                w.Bool(cachedMeshLodEnabled);
                w.F32(cachedMeshLodTolerance);
                w.I64(nextMeshCacheAttempt);
                RecordingMeshSource.WriteCache(w, frozenMeshCache, recMeshCaches, false);
                w.I32(weaponContactMeshOrder.Count);
                foreach (var address in weaponContactMeshOrder)
                {
                    var entry = weaponContactMeshes[address];
                    w.U64((ulong)address);
                    w.U32(entry.Entity);
                    w.U64((ulong)entry.Draw);
                    RecordingMeshSource.WriteCache(w, entry.Cache, recMeshCaches, false);
                }
            }
            RecIdentity(skeletons.TryGetSkeleton(targetCharacterAddress));
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    /// <summary>Closes this activation's file, if any.</summary>
    private void RecEndActivation(string reason)
    {
        var file = recFile;
        rec = null;
        recFile = null;
        if (file == null) return;
        try
        {
            RagdollRecordingSession.Close(file, reason);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "RagdollRec: closing the recording failed.");
        }
    }

    private void RecFault(Exception ex)
    {
        if (!recFaultLogged)
        {
            recFaultLogged = true;
            log.Warning(ex, "RagdollRec: a recorder helper failed; the recording continues without that record.");
        }
        try
        {
            var w = rec;
            if (w == null) return;
            using (w.Begin(RecordType.Note))
                w.Str("recorder fault: " + ex.GetType().Name + ": " + ex.Message);
        }
        catch
        {
            // Nothing more to do; never let the recorder disturb the frame.
        }
    }

    /// <summary>
    /// Exception filter of the recorder's own catch blocks. A filter runs before the using-scopes
    /// between the throw and the catch are disposed, so the record that was being built at that moment
    /// (if any) can still be marked: its scope then writes a note in its place instead of a record
    /// that stops half-way. Always lets the catch block run.
    /// </summary>
    private bool RecAbortPartial()
    {
        try { rec?.AbortRecord(); }
        catch { /* never let the recorder disturb the frame */ }
        return true;
    }

    private void RecLeave()
    {
        if (recDepth > 0) recDepth--;
    }

    private void OnRenderFrame()
    {
        if (rec == null)
        {
            OnRenderFrameCore();
            return;
        }
        recDepth++;
        try { OnRenderFrameCore(); }
        finally { RecLeave(); }
    }

    // ------------------------------------------------------------------ configuration

    private object? RecConfigValue(int index)
    {
        var property = RecConfigProperties[index];
        if (property == null) return "<missing property>";
        try
        {
            var value = property.GetValue(settings);
            return value switch
            {
                null => null,
                bool or int or uint or long or float or double or string => value,
                Enum e => Convert.ToInt32(e),
                System.Collections.IEnumerable list when value is not string => RecJoin(list),
                _ => System.Text.Json.JsonSerializer.Serialize(value, value.GetType()),
            };
        }
        catch (Exception ex)
        {
            return "<unreadable: " + ex.GetType().Name + ">";
        }
    }

    private static string RecJoin(System.Collections.IEnumerable list)
    {
        var text = new System.Text.StringBuilder("[");
        var first = true;
        foreach (var item in list)
        {
            if (!first) text.Append(',');
            first = false;
            text.Append(System.Text.Json.JsonSerializer.Serialize(item?.ToString()));
        }
        return text.Append(']').ToString();
    }

    private void RecConfigSnapshot(RagdollRecordingWriter w)
    {
        var prototypes = recConfigWatchPrototypes ??= Array.ConvertAll(RecConfigProperties,
            property => property == null ? null : RagdollRecordingWatch.For(property));
        var values = new object?[RecConfigNames.Length];
        recConfigWatches = new RagdollRecordingWatch?[RecConfigNames.Length];
        for (var i = 0; i < RecConfigNames.Length; i++)
        {
            values[i] = RecConfigValue(i);
            // The first look only remembers the value the snapshot holds.
            var watch = recConfigWatches[i] = prototypes[i]?.Clone();
            try { watch?.Changed(settings); }
            catch { recConfigWatches[i] = null; /* unreadable: the snapshot says so; nothing to watch */ }
        }
        using (w.Begin(RecordType.ConfigSnapshot))
        {
            w.List(RecConfigNames.Length * 2);
            for (var i = 0; i < RecConfigNames.Length; i++)
            {
                w.Str(RecConfigNames[i]);
                RecAny(w, values[i]);
            }
        }
    }

    /// <summary>
    /// Once per render frame. While nothing changed this allocates nothing: each watch reads its
    /// property through a bound getter and compares it in its own type (see RagdollRecordingWatch).
    /// The value is read the slow way, and written, only for a property that did change.
    /// </summary>
    private void RecConfigChanges(RagdollRecordingWriter w)
    {
        var watches = recConfigWatches;
        for (var i = 0; i < watches.Length; i++)
        {
            var watch = watches[i];
            if (watch == null) continue;
            bool changed;
            try { changed = watch.Changed(settings); }
            catch { changed = false; }
            if (!changed) continue;
            var now = RecConfigValue(i);
            using (w.Begin(RecordType.ConfigChange))
            {
                w.Str(RecConfigNames[i]);
                RecAny(w, now);
            }
        }
    }

    // ------------------------------------------------------------------ generic values

    private static void RecAny(RagdollRecordingWriter w, object? value)
    {
        switch (value)
        {
            case null: w.Null(); break;
            case bool v: w.Bool(v); break;
            case int v: w.I32(v); break;
            case uint v: w.U32(v); break;
            case short v: w.I32(v); break;
            case byte v: w.I32(v); break;
            case long v: w.I64(v); break;
            case ulong v: w.U64(v); break;
            case nint v: w.U64((ulong)v); break;
            case float v: w.F32(v); break;
            case double v: w.F64(v); break;
            case string v: w.Str(v); break;
            case Vector2 v: w.V2(v); break;
            case Vector3 v: w.V3(v); break;
            case Quaternion v: w.Quat(v); break;
            case Enum v: w.I32(Convert.ToInt32(v)); break;
            case float[] v: w.Floats(v); break;
            case int[] v: w.Ints(v); break;
            case Vector3[] v: w.V3s(v); break;
            case Quaternion[] v: w.Quats(v); break;
            case BodyHandle v: w.I32(v.Value); break;
            case TypedIndex v: w.U32(v.Packed); break;
            // A reference to something recorded elsewhere (a mesh, a LOD): only its presence.
            default: w.Str("<" + value.GetType().Name + ">"); break;
        }
    }

    /// <summary>Writes every public field of a struct or class as one list of (name, value) pairs.</summary>
    private static void RecFields(RagdollRecordingWriter w, object boxed)
    {
        var fields = RecFieldCache.GetOrAdd(boxed.GetType(),
            static type => type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        w.List(fields.Length * 2);
        foreach (var field in fields)
        {
            w.Str(field.Name);
            RecAny(w, field.GetValue(boxed));
        }
    }

    // ------------------------------------------------------------------ skeletons and actors

    /// <param name="skeleton">The target's skeleton as the surrounding code obtained it, or null. The model
    /// type is asked of the draw object only when the ragdoll code itself would ask at this point: once a
    /// skeleton with models and a Havok skeleton was obtained (the condition in StartStructuralMeshFit).
    /// Otherwise the record says -1.</param>
    private void RecIdentity(ISkeletonPose? skeleton)
    {
        var w = rec;
        if (w == null || targetCharacterAddress == nint.Zero) return;
        try
        {
            Span<byte> customize = stackalloc byte[0x1A];
            actors.ReadAppearance(targetCharacterAddress, customize, out var modelCharaId);
            actors.ReadModelType(targetCharacterAddress, skeleton, out var modelType, out var raceSex);
            using (w.Begin(RecordType.Identity))
            {
                w.U64((ulong)targetCharacterAddress);
                w.Bytes(customize);
                w.I32(modelCharaId);
                w.I32(modelType);
                w.I32(raceSex);
                w.F32(actors.GetFacing(targetCharacterAddress, ActorReadSite.Facing));
                w.V3(actors.GetPosition(targetCharacterAddress, ActorReadSite.Identity));
            }
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    private void RecTopology(RagdollRecordingWriter w, ulong actor, ISkeletonPose skeleton, int partial)
    {
        if (!skeleton.PartAvailable(partial)) return;
        var havok = skeleton.PartIdentity(partial);
        var bones = skeleton.PartModelPoseLength(partial);
        if (recTopologies.TryGetValue((actor, partial), out var seen) && seen == (havok, bones)) return;
        recTopologies[(actor, partial)] = (havok, bones);

        var nameCount = skeleton.PartBoneNameCount(partial);
        var parentCount = skeleton.PartParentCount(partial);
        var parents = new int[parentCount];
        for (var i = 0; i < parentCount; i++) parents[i] = skeleton.PartParent(partial, i);
        using (w.Begin(RecordType.SkeletonTopology))
        {
            w.U64(actor);
            w.I32(partial);
            w.I32(bones);
            w.I32(parentCount);
            w.I32(skeleton.PartConnectedBone(partial));
            w.I32(skeleton.PartConnectedParentBone(partial));
            w.List(nameCount);
            for (var i = 0; i < nameCount; i++) w.Str(skeleton.PartBoneName(partial, i));
            w.Ints(parents);
            w.Bytes(MemoryMarshal.AsBytes(skeleton.PartReferencePose(partial)));
        }
    }

    /// <summary>
    /// Captures a skeleton as the surrounding code is about to read it (or, for PoseOut, has just
    /// written it): the root transform, the scale sources and the model pose of the body skeleton
    /// (and of every attached partial skeleton when asked).
    /// </summary>
    private void RecSkeleton(RecordType type, PosePurpose purpose, nint address, ISkeletonPose? access, bool allPartials)
    {
        var w = rec;
        if (w == null) return;
        try
        {
            var actor = (ulong)address;
            if (access == null || !access.HasRoot)
            {
                using (w.Begin(type))
                {
                    w.U64(actor);
                    w.I32((int)purpose);
                    w.Bool(false);
                }
                return;
            }

            var skel = access;
            var partials = allPartials
                ? Math.Clamp(skel.PartCount, 1, 64)
                : 1;
            for (var p = 0; p < partials; p++)
                RecTopology(w, actor, skel, p);

            var drawScaleValid = TryGetDrawObjectScale(address, out var drawScale);
            var root = skel.Root;
            using (w.Begin(type))
            {
                w.U64(actor);
                w.I32((int)purpose);
                w.Bool(true);
                w.V3(root.Position);
                w.Quat(root.Rotation);
                w.V3(root.Scale);
                w.Bool(drawScaleValid);
                w.V3(drawScale);
                w.I32(partials);
                for (var p = 0; p < partials; p++)
                {
                    w.I32(p);
                    if (!skel.PartHasPose(p))
                    {
                        w.I32(2);
                        continue;
                    }
                    var notInSync = skel.PartInSync(p) ? 0 : 4;
                    var bytes = MemoryMarshal.AsBytes((ReadOnlySpan<BoneQs>)skel.PartModelPose(p));
                    var key = (actor, (int)purpose, p);
                    if (recLastPose.TryGetValue(key, out var last) && bytes.SequenceEqual(last))
                    {
                        w.I32(1 | notInSync);
                        continue;
                    }
                    if (last == null || last.Length != bytes.Length)
                        recLastPose[key] = last = new byte[bytes.Length];
                    bytes.CopyTo(last);
                    w.I32(notInSync);
                    w.Bytes(bytes);
                }
            }
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    private void RecActorPosition(ActorSite site, nint address, Vector3 position)
    {
        var w = rec;
        if (w == null) return;
        using (w.Begin(RecordType.ActorPosition))
        {
            w.I32((int)site);
            w.U64((ulong)address);
            w.V3(position);
        }
    }

    private void RecActorTransform(ActorSite site, nint address, Vector3 position, Quaternion rotation, bool scaleValid, Vector3 scale)
    {
        var w = rec;
        if (w == null) return;
        using (w.Begin(RecordType.ActorTransform))
        {
            w.I32((int)site);
            w.U64((ulong)address);
            w.V3(position);
            w.Quat(rotation);
            w.Bool(scaleValid);
            w.V3(scale);
        }
    }

    private void RecActorList(string site, IReadOnlyList<nint> actors)
    {
        var w = rec;
        if (w == null) return;
        using (w.Begin(RecordType.ActorList))
        {
            w.Str(site);
            w.List(actors.Count);
            for (var i = 0; i < actors.Count; i++) w.U64((ulong)actors[i]);
        }
    }

    /// <summary>The selected NPCs in the order the collision build walks them (those without a character last, flagged).</summary>
    private void RecSelectedNpcs(string site, List<ColliderCandidate> selected)
    {
        if (rec == null || !colliders.HasSelector) return;
        try
        {
            var present = new List<nint>();
            var missing = new List<nint>();
            foreach (var npc in selected)
                (!npc.HasCharacter ? missing : present).Add(npc.Address);
            RecActorList(site, present);
            if (missing.Count > 0) RecActorList(site + ".withoutCharacter", missing);
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    private void RecWeaponSample(Vector3 position, Quaternion rotation)
    {
        var w = rec;
        if (w == null) return;
        using (w.Begin(RecordType.WeaponSample))
        {
            w.Bool(true);
            w.V3(position);
            w.Quat(rotation);
        }
    }

    private void RecBoneWorldPosition(string site, nint address, string bone, Vector3? position)
    {
        var w = rec;
        if (w == null) return;
        using (w.Begin(RecordType.BoneWorldPosition))
        {
            w.Str(site);
            w.U64((ulong)address);
            w.Str(bone);
            w.Bool(position.HasValue);
            w.V3(position ?? Vector3.Zero);
        }
    }

    // World queries, random draws and clock stamps are recorded by the decorators in
    // Hosting/RecordingDecorators.cs.

    // ------------------------------------------------------------------ decisions

    private bool RecDecision(string site, bool value)
    {
        var w = rec;
        if (w == null) return value;
        using (w.Begin(RecordType.Decision))
        {
            w.Str(site);
            w.Bool(value);
        }
        return value;
    }


    private void RecEvent(string name, float value)
    {
        var w = rec;
        if (w == null) return;
        using (w.Begin(RecordType.Event))
        {
            w.Str(name);
            w.F32(value);
        }
    }

    // ------------------------------------------------------------------ frames

    /// <summary>Once per render frame while the ragdoll is active, right after the frame's dt is known.</summary>
    private void RecRenderFrame(float dt)
    {
        var w = rec;
        var file = recFile;
        if (w == null || file == null) return;
        if (!w.IsOpen)
        {
            // The session was stopped (or the disk failed): drop the reference so the call sites go quiet.
            RecEndActivation(w.Failed ? "write failed" : "session stopped");
            return;
        }
        // The byte limits are enforced by the writer for every record (it refuses the record that would
        // cross one, and everything after it). Here the refusal is noticed and the file is closed.
        if (RagdollRecordingSession.SessionBytesExhausted)
        {
            RagdollRecordingSession.CloseAllAtSessionLimit();
            RecEndActivation(RagdollRecordingSession.SessionBytesReason);
            return;
        }
        if (file.Seconds > RagdollRecordingSession.SecondsLimit || w.FileLimitReached)
        {
            RecEndActivation(w.FileLimitReached
                ? $"size limit of {RagdollRecordingSession.MaxBytesPerFile / (1024 * 1024)} MiB reached"
                : $"time limit of {RagdollRecordingSession.MaxSecondsPerFile:F0} s reached");
            return;
        }
        try
        {
            RecConfigChanges(w);
            file.RenderFrames = ++recRenderFrame;
            using (w.Begin(RecordType.RenderFrame))
            {
                w.I32(recRenderFrame);
                w.F32(dt);
                w.F32(elapsed);
                w.Bool(physicsStarted);
                w.F32(attackStrikeTimer);
                w.F32(strikePower);
                w.U64((ulong)strikeColliderAddress);
            }
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    private void RecPreFrame()
    {
        if (rec == null) return;
        try
        {
            RecSkeleton(RecordType.SkeletonPose, PosePurpose.PreFrame, targetCharacterAddress,
                skeletons.TryGetSkeleton(targetCharacterAddress, SkeletonReadSite.PreFrame), false);
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    private void RecTriggerInputs(RagdollAnimationSample? animation, float? middleHeight, float? motion)
    {
        var w = rec;
        if (w == null) return;
        using (w.Begin(RecordType.TriggerInputs))
        {
            w.Bool(animation.HasValue);
            w.U64((ulong)(animation?.Id ?? 0));
            w.F32(animation?.Time ?? 0f);
            w.F32(animation?.Duration ?? 0f);
            w.Bool(middleHeight.HasValue);
            w.F32(middleHeight ?? 0f);
            w.Bool(motion.HasValue);
            w.F32(motion ?? 0f);
        }
    }

    private void RecFrameBegin(float dt, ISkeletonPose skel)
    {
        var w = rec;
        var file = recFile;
        if (w == null || file == null) return;
        try
        {
            file.PhysicsFrames = ++recPhysicsFrame;
            using (w.Begin(RecordType.FrameBegin))
            {
                w.I32(recPhysicsFrame);
                w.F32(dt);
                w.F32(physicsAccumulator);
                w.Bool(prevAllAsleep);
                w.U32(actors.GetEntityId(targetCharacterAddress));
                w.I32(actors.GetObjectIndex(targetCharacterAddress));
                w.Bool(followWasActive);
                w.F32(impactFreezeRemaining);
                w.F32(biomechanicalSettleRemaining);
                w.I32(grabSlots.Count);
                w.I32(externalBodies.Count);
                w.I32(externalRigs.Count);
            }
            RecSkeleton(RecordType.SkeletonPose, PosePurpose.FrameIn, targetCharacterAddress, skel, true);
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    /// <summary>After each fixed step (and everything the controller does around it): every body's state.</summary>
    private void RecStep(int substep, bool steppedExternally)
    {
        var w = rec;
        var file = recFile;
        if (w == null || file == null || simulation == null) return;
        try
        {
            file.FixedSteps = ++recTick;
            RagdollRecordingBepu.WriteBodyStates(w, simulation, recTick, recPhysicsFrame, substep, steppedExternally,
                ref recHandles, ref recFlags, ref recStates);
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    private void RecFrameEnd(int substeps, bool resting, float renderAlpha)
    {
        var w = rec;
        if (w == null) return;
        using (w.Begin(RecordType.FrameEnd))
        {
            w.I32(recPhysicsFrame);
            w.I32(substeps);
            w.Bool(resting);
            w.F32(physicsAccumulator);
            w.F32(renderAlpha);
            w.I32(recTick);
        }
    }

    // ------------------------------------------------------------------ InitializePhysics

    private void RecInitBegin(ISkeletonPose skel)
    {
        var w = rec;
        if (w == null) return;
        try
        {
            recFitOutcome = 0;
            var count = handoffSampleValid && handoffPrevPos != null && handoffPrevRot != null
                ? Math.Min(handoffPrevCount, Math.Min(handoffPrevPos.Length, handoffPrevRot.Length))
                : 0;
            using (w.Begin(RecordType.InitBegin))
            {
                w.Bool(handoffSampleValid);
                w.F32(handoffSampleDt);
                w.V3(handoffSkelPos);
                w.Quat(handoffSkelRot);
                w.V3(handoffSkelScale);
                w.I32(handoffPrevCount);
                w.V3s(count > 0 ? handoffPrevPos.AsSpan(0, count) : default);
                w.Quats(count > 0 ? handoffPrevRot.AsSpan(0, count) : default);
            }
            RecIdentity(skel);
            RecSkeleton(RecordType.SkeletonPose, PosePurpose.Init, targetCharacterAddress, skel, true);
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    private void RecBoneDefs(RagdollBoneDef[] defs, Dictionary<string, int> nameToIndex, bool generic)
    {
        var w = rec;
        if (w == null) return;
        try
        {
            using (w.Begin(RecordType.BoneDefs))
            {
                w.Bool(generic);
                w.I32(defs.Length);
                foreach (var def in defs)
                {
                    RecFields(w, def);
                    w.I32(nameToIndex.TryGetValue(def.Name, out var index) ? index : -1);
                }
            }
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    private void RecStructuralFits(Dictionary<string, StructuralMeshFitter.Fit> fits)
    {
        var w = rec;
        if (w == null) return;
        try
        {
            using (w.Begin(RecordType.StructuralFits))
            {
                w.I32(recFitOutcome);
                w.I32(fits.Count);
                foreach (var entry in fits)
                {
                    w.Str(entry.Key);
                    RecFields(w, entry.Value);
                }
            }
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    /// <summary>The cached bone-local surface of a character, the first time this recording sees that cache.</summary>
    private void RecMeshCache(FrozenMeshGeometryCache? cache, string use)
    {
        var w = rec;
        if (w == null) return;
        try
        {
            using (w.Begin(RecordType.MeshCache))
            {
                w.Str(use);
                RecordingMeshSource.WriteCache(w, cache, recMeshCaches, true);
            }
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    private void RecMeshLod(string site, string bone, MeshCollisionLodResult lod)
    {
        var w = rec;
        if (w == null) return;
        try
        {
            using (w.Begin(RecordType.MeshLod))
            {
                w.Str(site);
                w.Str(bone);
                w.V3s(lod.Geometry.Vertices);
                w.Ints(lod.Geometry.Indices);
                w.Ints(lod.SourceVertexIndices);
                w.I32(lod.SourceFaceCount);
            }
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    private void RecMeshUse(string site, string bone, BodyMeshGeometry geometry)
    {
        var w = rec;
        if (w == null) return;
        try
        {
            using (w.Begin(RecordType.MeshLod))
            {
                w.Str(site);
                w.Str(bone);
                w.V3s(geometry.Vertices);
                w.Ints(geometry.Indices);
                w.Ints(default);
                w.I32(geometry.Indices.Length / 3);
            }
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    private void RecTriangles(string use, List<Triangle> triangles, Vector3 scale)
    {
        var w = rec;
        if (w == null) return;
        try
        {
            using (w.Begin(RecordType.Triangles))
            {
                w.Str(use);
                w.V3(scale);
                w.V3s(MemoryMarshal.Cast<Triangle, Vector3>(CollectionsMarshal.AsSpan(triangles)));
            }
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    private void RecHairMesh(HairMeshBinding? binding)
    {
        var w = rec;
        if (w == null) return;
        try
        {
            using (w.Begin(RecordType.HairMesh))
            {
                w.Bool(binding != null);
                w.Bool(animatedHair);
                w.I32(binding?.VisibleVertices ?? 0);
                w.I32(binding?.Samples.Count ?? 0);
                if (binding == null) return;
                foreach (var region in binding.Samples)
                {
                    var samples = region.Value;
                    var positions = new Vector3[samples.Length];
                    var counts = new int[samples.Length];
                    var total = 0;
                    for (var i = 0; i < samples.Length; i++)
                    {
                        positions[i] = samples[i].Position;
                        counts[i] = samples[i].Influences.Length;
                        total += counts[i];
                    }
                    var weights = new float[total];
                    w.Str(region.Key);
                    w.V3s(positions);
                    w.Ints(counts);
                    w.List(total);
                    var at = 0;
                    foreach (var sample in samples)
                        foreach (var influence in sample.Influences)
                        {
                            w.Str(influence.Bone);
                            weights[at++] = influence.Weight;
                        }
                    w.Floats(weights);
                }
            }
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    private void RecInitEnd(bool ok, int solverIterations, int solverSubsteps, bool generic)
    {
        var w = rec;
        if (w == null) return;
        try
        {
            using (w.Begin(RecordType.RigBodies))
            {
                w.I32(ragdollBones.Count);
                foreach (var rb in ragdollBones)
                {
                    w.Str(rb.Name);
                    w.I32(rb.BoneIndex);
                    w.I32(rb.BodyHandle.Value);
                    RecFields(w, rb);
                }
            }
            using (w.Begin(RecordType.InitEnd))
            {
                w.Bool(ok);
                w.I32(ragdollBones.Count);
                w.F32(groundY);
                w.F32(realGroundY);
                w.V3(skelWorldPos);
                w.Quat(skelWorldRot);
                w.V3(skelWorldScale);
                w.Bool(generic);
                w.I32(solverIterations);
                w.I32(solverSubsteps);
                w.I32(simulation?.Bodies.CountBodies() ?? 0);
                w.I32(simulation?.Statics.Count ?? 0);
                w.F32(savedOverallSpeed);
                w.I32(npcCollisionStates.Count);
                w.I32(hairRigChains.Count);
            }
            if (simulation != null)
                RagdollRecordingBepu.WriteBodyStates(w, simulation, 0, recPhysicsFrame, 0, false,
                    ref recHandles, ref recFlags, ref recStates);
        }
        catch (Exception ex) when (RecAbortPartial())
        {
            RecFault(ex);
        }
    }

    // ------------------------------------------------------------------ calls from the rest of the plugin

    private RagdollRecordingWriter.Scope RecCall(RagdollRecordingWriter w, string method)
    {
        var scope = w.Begin(RecordType.ApiCall);
        try
        {
            w.I32(recDepth);
            w.Str(method);
            return scope;
        }
        catch
        {
            // The caller never receives the scope, so it could not finish it: do that here (as a
            // discarded record), or the writer's lock would stay held.
            w.AbortRecord();
            scope.Dispose();
            throw;
        }
    }

    private static bool RecReturn(RagdollRecordingWriter w, string method, bool value)
    {
        using (w.Begin(RecordType.ApiReturn))
        {
            w.Str(method);
            w.Bool(value);
        }
        return value;
    }

    // The three helpers below write an argument inside a call record. They read objects handed in by
    // the caller, so they are the only part of such a record that can throw; when one does, the record
    // is marked incomplete (the writer puts a note in its place) and the call itself goes ahead.

    private static void RecBody(RagdollRecordingWriter w, ExternalBodyHandle? handle)
    {
        if (handle == null) { w.Null(); return; }
        try
        {
            w.List(3);
            w.I32(handle.Body.Value);
            w.I32(handle.Generation);
            w.Bool(handle.Removed);
        }
        catch { w.AbortRecord(); }
    }

    private static void RecRig(RagdollRecordingWriter w, ExternalRigHandle? handle)
    {
        if (handle == null) { w.Null(); return; }
        try
        {
            var bodies = new int[handle.Bodies.Count];
            for (var i = 0; i < bodies.Length; i++) bodies[i] = handle.Bodies[i].Body.Value;
            w.List(3);
            w.I32(handle.Generation);
            w.Bool(handle.Removed);
            w.Ints(bodies);
        }
        catch { w.AbortRecord(); }
    }

    private static void RecParts(RagdollRecordingWriter w, IReadOnlyList<ExternalShapePart>? parts)
    {
        if (parts == null) { w.Null(); return; }
        try
        {
            w.List(parts.Count * 3);
            for (var i = 0; i < parts.Count; i++)
            {
                w.V3(parts[i].HalfExtents);
                w.V3(parts[i].LocalPosition);
                w.Quat(parts[i].LocalOrientation);
            }
        }
        catch { w.AbortRecord(); }
    }

    public bool TryCreateExternalDynamicBody(IReadOnlyList<ExternalShapePart> parts, float mass,
        Vector3 position, Quaternion orientation, Vector3 linearVelocity, Vector3 angularVelocity,
        out ExternalBodyHandle? handle)
    {
        var w = rec;
        if (w == null)
            return TryCreateExternalDynamicBodyCore(parts, mass, position, orientation, linearVelocity, angularVelocity, out handle);
        using (RecCall(w, nameof(TryCreateExternalDynamicBody)))
        {
            RecParts(w, parts); w.F32(mass); w.V3(position); w.Quat(orientation); w.V3(linearVelocity); w.V3(angularVelocity);
        }
        recDepth++;
        try
        {
            var ok = TryCreateExternalDynamicBodyCore(parts, mass, position, orientation, linearVelocity, angularVelocity, out handle);
            using (w.Begin(RecordType.ApiReturn)) { w.Str(nameof(TryCreateExternalDynamicBody)); w.Bool(ok); RecBody(w, handle); }
            return ok;
        }
        finally { RecLeave(); }
    }

    public bool TryCreateExternalRig(
        IReadOnlyList<ExternalRigBodySpec> bodySpecs,
        IReadOnlyList<ExternalRigJointSpec> jointSpecs,
        out ExternalRigHandle? handle,
        bool collideWithRagdoll = true)
    {
        var w = rec;
        if (w == null) return TryCreateExternalRigCore(bodySpecs, jointSpecs, out handle, collideWithRagdoll);
        try
        {
            using (RecCall(w, nameof(TryCreateExternalRig)))
            {
                w.I32(bodySpecs?.Count ?? -1);
                for (var i = 0; bodySpecs != null && i < bodySpecs.Count; i++)
                {
                    var spec = bodySpecs[i];
                    w.Str(spec.Name); RecParts(w, spec.Parts); w.F32(spec.Mass); w.V3(spec.Position); w.Quat(spec.Orientation);
                    w.V3(spec.LinearVelocity); w.V3(spec.AngularVelocity);
                }
                w.I32(jointSpecs?.Count ?? -1);
                for (var i = 0; jointSpecs != null && i < jointSpecs.Count; i++)
                {
                    var joint = jointSpecs[i];
                    w.I32((int)joint.Kind); w.I32(joint.ParentIndex); w.I32(joint.ChildIndex); w.V3(joint.AnchorWorld);
                    w.F32(joint.SwingLimit); w.F32(joint.InitialSwingFactor); w.F32(joint.PoseGuideMaxForce);
                    w.F32(joint.PoseGuideFrequency); w.V3(joint.LocalOffsetChild); w.V3(joint.LocalOffsetParent);
                    w.F32(joint.MinDistance); w.F32(joint.MaxDistance); w.F32(joint.DistanceFrequency);
                    w.F32(joint.DistanceDamping); w.F32(joint.JointFriction);
                }
                w.Bool(collideWithRagdoll);
            }
        }
        catch (Exception ex) when (RecAbortPartial()) { RecFault(ex); }
        recDepth++;
        try
        {
            var ok = TryCreateExternalRigCore(bodySpecs!, jointSpecs!, out handle, collideWithRagdoll);
            using (w.Begin(RecordType.ApiReturn)) { w.Str(nameof(TryCreateExternalRig)); w.Bool(ok); RecRig(w, handle); }
            return ok;
        }
        finally { RecLeave(); }
    }

    public bool TrySetExternalBodyShape(ExternalBodyHandle? handle,
        IReadOnlyList<ExternalShapePart> parts, float mass)
    {
        var w = rec;
        if (w == null) return TrySetExternalBodyShapeCore(handle, parts, mass);
        using (RecCall(w, nameof(TrySetExternalBodyShape))) { RecBody(w, handle); RecParts(w, parts); w.F32(mass); }
        recDepth++;
        try { return RecReturn(w, nameof(TrySetExternalBodyShape), TrySetExternalBodyShapeCore(handle, parts, mass)); }
        finally { RecLeave(); }
    }

    public bool TrySetExternalRigBodyPose(ExternalRigHandle? handle, int bodyIndex,
        Vector3 position, Quaternion orientation, Vector3 linearVelocity, Vector3 angularVelocity)
    {
        var w = rec;
        if (w == null) return TrySetExternalRigBodyPoseCore(handle, bodyIndex, position, orientation, linearVelocity, angularVelocity);
        using (RecCall(w, nameof(TrySetExternalRigBodyPose)))
        {
            RecRig(w, handle); w.I32(bodyIndex); w.V3(position); w.Quat(orientation); w.V3(linearVelocity); w.V3(angularVelocity);
        }
        recDepth++;
        try
        {
            return RecReturn(w, nameof(TrySetExternalRigBodyPose),
                TrySetExternalRigBodyPoseCore(handle, bodyIndex, position, orientation, linearVelocity, angularVelocity));
        }
        finally { RecLeave(); }
    }

    public bool SetExternalRigBodyRagdollContact(ExternalRigHandle? handle, int bodyIndex, bool collide)
    {
        var w = rec;
        if (w == null) return SetExternalRigBodyRagdollContactCore(handle, bodyIndex, collide);
        using (RecCall(w, nameof(SetExternalRigBodyRagdollContact))) { RecRig(w, handle); w.I32(bodyIndex); w.Bool(collide); }
        recDepth++;
        try { return RecReturn(w, nameof(SetExternalRigBodyRagdollContact), SetExternalRigBodyRagdollContactCore(handle, bodyIndex, collide)); }
        finally { RecLeave(); }
    }

    public bool TrySetExternalRigBodyShape(ExternalRigHandle? handle, int bodyIndex,
        IReadOnlyList<ExternalShapePart> parts, float mass)
    {
        var w = rec;
        if (w == null) return TrySetExternalRigBodyShapeCore(handle, bodyIndex, parts, mass);
        using (RecCall(w, nameof(TrySetExternalRigBodyShape))) { RecRig(w, handle); w.I32(bodyIndex); RecParts(w, parts); w.F32(mass); }
        recDepth++;
        try { return RecReturn(w, nameof(TrySetExternalRigBodyShape), TrySetExternalRigBodyShapeCore(handle, bodyIndex, parts, mass)); }
        finally { RecLeave(); }
    }

    public bool TryApplyExternalRigVelocityDelta(ExternalRigHandle? handle, Vector3 velocityDelta)
    {
        var w = rec;
        if (w == null) return TryApplyExternalRigVelocityDeltaCore(handle, velocityDelta);
        using (RecCall(w, nameof(TryApplyExternalRigVelocityDelta))) { RecRig(w, handle); w.V3(velocityDelta); }
        recDepth++;
        try { return RecReturn(w, nameof(TryApplyExternalRigVelocityDelta), TryApplyExternalRigVelocityDeltaCore(handle, velocityDelta)); }
        finally { RecLeave(); }
    }

    public bool TryDampenExternalRigVelocity(ExternalRigHandle? handle,
        float horizontalScale, float verticalScale, float angularScale)
    {
        var w = rec;
        if (w == null) return TryDampenExternalRigVelocityCore(handle, horizontalScale, verticalScale, angularScale);
        using (RecCall(w, nameof(TryDampenExternalRigVelocity))) { RecRig(w, handle); w.F32(horizontalScale); w.F32(verticalScale); w.F32(angularScale); }
        recDepth++;
        try { return RecReturn(w, nameof(TryDampenExternalRigVelocity), TryDampenExternalRigVelocityCore(handle, horizontalScale, verticalScale, angularScale)); }
        finally { RecLeave(); }
    }

    public bool RelaxExternalRigSwingLimits(ExternalRigHandle? handle, float factor)
    {
        var w = rec;
        if (w == null) return RelaxExternalRigSwingLimitsCore(handle, factor);
        using (RecCall(w, nameof(RelaxExternalRigSwingLimits))) { RecRig(w, handle); w.F32(factor); }
        recDepth++;
        try { return RecReturn(w, nameof(RelaxExternalRigSwingLimits), RelaxExternalRigSwingLimitsCore(handle, factor)); }
        finally { RecLeave(); }
    }

    public bool ApplyExternalRigPoseGuidance(ExternalRigHandle? handle, float strength)
    {
        var w = rec;
        if (w == null) return ApplyExternalRigPoseGuidanceCore(handle, strength);
        using (RecCall(w, nameof(ApplyExternalRigPoseGuidance))) { RecRig(w, handle); w.F32(strength); }
        recDepth++;
        try { return RecReturn(w, nameof(ApplyExternalRigPoseGuidance), ApplyExternalRigPoseGuidanceCore(handle, strength)); }
        finally { RecLeave(); }
    }

    public bool TrySetExternalBodyRotation(ExternalBodyHandle? handle, Quaternion orientation, float angularVelocityScale)
    {
        var w = rec;
        if (w == null) return TrySetExternalBodyRotationCore(handle, orientation, angularVelocityScale);
        using (RecCall(w, nameof(TrySetExternalBodyRotation))) { RecBody(w, handle); w.Quat(orientation); w.F32(angularVelocityScale); }
        recDepth++;
        try { return RecReturn(w, nameof(TrySetExternalBodyRotation), TrySetExternalBodyRotationCore(handle, orientation, angularVelocityScale)); }
        finally { RecLeave(); }
    }

    public bool TryApplyExternalVelocityDelta(ExternalBodyHandle? handle, Vector3 velocityDelta)
    {
        var w = rec;
        if (w == null) return TryApplyExternalVelocityDeltaCore(handle, velocityDelta);
        using (RecCall(w, nameof(TryApplyExternalVelocityDelta))) { RecBody(w, handle); w.V3(velocityDelta); }
        recDepth++;
        try { return RecReturn(w, nameof(TryApplyExternalVelocityDelta), TryApplyExternalVelocityDeltaCore(handle, velocityDelta)); }
        finally { RecLeave(); }
    }

    public bool TryDampenExternalBodyVelocity(ExternalBodyHandle? handle,
        float horizontalScale, float verticalScale, float angularScale)
    {
        var w = rec;
        if (w == null) return TryDampenExternalBodyVelocityCore(handle, horizontalScale, verticalScale, angularScale);
        using (RecCall(w, nameof(TryDampenExternalBodyVelocity))) { RecBody(w, handle); w.F32(horizontalScale); w.F32(verticalScale); w.F32(angularScale); }
        recDepth++;
        try { return RecReturn(w, nameof(TryDampenExternalBodyVelocity), TryDampenExternalBodyVelocityCore(handle, horizontalScale, verticalScale, angularScale)); }
        finally { RecLeave(); }
    }

    public void RemoveExternalBody(ExternalBodyHandle? handle)
    {
        var w = rec;
        if (w == null) { RemoveExternalBodyCore(handle); return; }
        using (RecCall(w, nameof(RemoveExternalBody))) RecBody(w, handle);
        recDepth++;
        try { RemoveExternalBodyCore(handle); }
        finally { RecLeave(); }
    }

    public void RemoveExternalRig(ExternalRigHandle? handle)
    {
        var w = rec;
        if (w == null) { RemoveExternalRigCore(handle); return; }
        using (RecCall(w, nameof(RemoveExternalRig))) RecRig(w, handle);
        recDepth++;
        try { RemoveExternalRigCore(handle); }
        finally { RecLeave(); }
    }

    public void SetDismemberedBones(IEnumerable<string> bones)
    {
        var w = rec;
        if (w == null) { SetDismemberedBonesCore(bones); return; }
        // Same order of effects as with recording off: the core clears its table first and only then
        // walks the caller's sequence, once (the sequence may be lazy, or may read that very table).
        // The items are noted as the core takes them, so this is the one call whose record is written
        // after the call ran. It makes no other record, so nothing can come between the two.
        var seen = new List<string?>();
        recDepth++;
        try { SetDismemberedBonesCore(bones == null ? null! : RecTee(bones, seen)); }
        finally
        {
            RecLeave();
            try
            {
                using (RecCall(w, nameof(SetDismemberedBones)))
                {
                    if (bones == null) w.Null();
                    else
                    {
                        w.List(seen.Count);
                        foreach (var bone in seen) w.Str(bone);
                    }
                }
            }
            catch (Exception ex) when (RecAbortPartial())
            {
                RecFault(ex);
            }
        }
    }

    /// <summary>Hands a sequence on unchanged and notes each item as the consumer takes it.</summary>
    private static IEnumerable<string> RecTee(IEnumerable<string> source, List<string?> seen)
    {
        foreach (var item in source)
        {
            seen.Add(item);
            yield return item;
        }
    }

    public void Deactivate()
    {
        var w = rec;
        if (w == null) { DeactivateCore(); return; }
        using (RecCall(w, nameof(Deactivate))) { }
        recDepth++;
        try { DeactivateCore(); }
        finally
        {
            RecLeave();
            RecEndActivation("deactivated");
        }
    }

    public void SetTraversalMassMultiplier(float multiplier)
    {
        var w = rec;
        if (w == null) { SetTraversalMassMultiplierCore(multiplier); return; }
        using (RecCall(w, nameof(SetTraversalMassMultiplier))) w.F32(multiplier);
        recDepth++;
        try { SetTraversalMassMultiplierCore(multiplier); }
        finally { RecLeave(); }
    }

    public int? CreateGrabConstraint(string boneName, Vector3 initialTarget, nint grabbingNpcAddress = 0, float maxForce = 1000f, float maxSpeed = 50f, float springFreq = 120f)
    {
        var w = rec;
        if (w == null) return CreateGrabConstraintCore(boneName, initialTarget, grabbingNpcAddress, maxForce, maxSpeed, springFreq);
        using (RecCall(w, nameof(CreateGrabConstraint)))
        {
            w.Str(boneName); w.V3(initialTarget); w.U64((ulong)grabbingNpcAddress); w.F32(maxForce); w.F32(maxSpeed); w.F32(springFreq);
            w.Bool(GrabRigid); // read by the call, set through the property
        }
        recDepth++;
        try
        {
            var slot = CreateGrabConstraintCore(boneName, initialTarget, grabbingNpcAddress, maxForce, maxSpeed, springFreq);
            using (w.Begin(RecordType.ApiReturn)) { w.Str(nameof(CreateGrabConstraint)); w.Bool(slot.HasValue); w.I32(slot ?? 0); }
            return slot;
        }
        finally { RecLeave(); }
    }

    /// <summary>
    /// Work out what a grab on this bone should actually take hold of: the bone's own body, or the
    /// body of the nearest ancestor that has one, with the offset of the requested bone in that
    /// body's frame (see TryResolveGrabAttachmentCore).
    ///
    /// It changes nothing, but it reads a bone's world position from the game, so it is recorded as a
    /// call: the BoneWorldPosition record it makes then always belongs to a call record (this one when
    /// called from outside, nested in CreateGrabConstraint's otherwise).
    /// </summary>
    public bool TryResolveGrabAttachment(string boneName, out BodyHandle body, out Vector3 localOffset)
    {
        var w = rec;
        if (w == null) return TryResolveGrabAttachmentCore(boneName, out body, out localOffset);
        using (RecCall(w, nameof(TryResolveGrabAttachment))) w.Str(boneName);
        recDepth++;
        try
        {
            var ok = TryResolveGrabAttachmentCore(boneName, out body, out localOffset);
            using (w.Begin(RecordType.ApiReturn)) { w.Str(nameof(TryResolveGrabAttachment)); w.Bool(ok); w.I32(body.Value); w.V3(localOffset); }
            return ok;
        }
        finally { RecLeave(); }
    }

    public void SetGrabTuning(int slotId, float maxForce, float maxSpeed, float springFreq)
    {
        var w = rec;
        if (w == null) { SetGrabTuningCore(slotId, maxForce, maxSpeed, springFreq); return; }
        using (RecCall(w, nameof(SetGrabTuning))) { w.I32(slotId); w.F32(maxForce); w.F32(maxSpeed); w.F32(springFreq); }
        recDepth++;
        try { SetGrabTuningCore(slotId, maxForce, maxSpeed, springFreq); }
        finally { RecLeave(); }
    }

    public void UpdateGrabTarget(int slotId, Vector3 worldTarget)
    {
        var w = rec;
        if (w == null) { UpdateGrabTargetCore(slotId, worldTarget); return; }
        using (RecCall(w, nameof(UpdateGrabTarget))) { w.I32(slotId); w.V3(worldTarget); }
        recDepth++;
        try { UpdateGrabTargetCore(slotId, worldTarget); }
        finally { RecLeave(); }
    }

    public void RemoveGrabConstraint(int slotId)
    {
        var w = rec;
        if (w == null) { RemoveGrabConstraintCore(slotId); return; }
        using (RecCall(w, nameof(RemoveGrabConstraint))) w.I32(slotId);
        recDepth++;
        try { RemoveGrabConstraintCore(slotId); }
        finally { RecLeave(); }
    }

    public void RequestDirectedKneelPitchOnReady(float duration, float footForce, float pelvisForce,
        float drop, float forward, float pitchDegrees)
    {
        var w = rec;
        if (w == null) { RequestDirectedKneelPitchOnReadyCore(duration, footForce, pelvisForce, drop, forward, pitchDegrees); return; }
        using (RecCall(w, nameof(RequestDirectedKneelPitchOnReady)))
        {
            w.F32(duration); w.F32(footForce); w.F32(pelvisForce); w.F32(drop); w.F32(forward); w.F32(pitchDegrees);
        }
        recDepth++;
        try { RequestDirectedKneelPitchOnReadyCore(duration, footForce, pelvisForce, drop, forward, pitchDegrees); }
        finally { RecLeave(); }
    }

    public void RequestProfileDirectedCollapseOnReady(string profileId)
    {
        var w = rec;
        if (w == null) { RequestProfileDirectedCollapseOnReadyCore(profileId); return; }
        using (RecCall(w, nameof(RequestProfileDirectedCollapseOnReady))) w.Str(profileId);
        recDepth++;
        try { RequestProfileDirectedCollapseOnReadyCore(profileId); }
        finally { RecLeave(); }
    }

    public bool BeginProfileDirectedCollapseSpike(string profileId)
    {
        var w = rec;
        if (w == null) return BeginProfileDirectedCollapseSpikeCore(profileId);
        using (RecCall(w, nameof(BeginProfileDirectedCollapseSpike))) w.Str(profileId);
        recDepth++;
        try { return RecReturn(w, nameof(BeginProfileDirectedCollapseSpike), BeginProfileDirectedCollapseSpikeCore(profileId)); }
        finally { RecLeave(); }
    }

    public bool BeginDirectedKneelPitchSpike(float duration, float footForce, float pelvisForce,
        float drop, float forward, float pitchDegrees)
    {
        var w = rec;
        if (w == null) return BeginDirectedKneelPitchSpikeCore(duration, footForce, pelvisForce, drop, forward, pitchDegrees);
        using (RecCall(w, nameof(BeginDirectedKneelPitchSpike)))
        {
            w.F32(duration); w.F32(footForce); w.F32(pelvisForce); w.F32(drop); w.F32(forward); w.F32(pitchDegrees);
        }
        recDepth++;
        try
        {
            return RecReturn(w, nameof(BeginDirectedKneelPitchSpike),
                BeginDirectedKneelPitchSpikeCore(duration, footForce, pelvisForce, drop, forward, pitchDegrees));
        }
        finally { RecLeave(); }
    }

    public void StopDirectedCollapseSpike()
    {
        var w = rec;
        if (w == null) { StopDirectedCollapseSpikeCore(); return; }
        using (RecCall(w, nameof(StopDirectedCollapseSpike))) { }
        recDepth++;
        try { StopDirectedCollapseSpikeCore(); }
        finally { RecLeave(); }
    }

    public void RequestEntryConditionedKneePowerLossOnReady()
    {
        var w = rec;
        if (w == null) { RequestEntryConditionedKneePowerLossOnReadyCore(); return; }
        using (RecCall(w, nameof(RequestEntryConditionedKneePowerLossOnReady))) { }
        recDepth++;
        try { RequestEntryConditionedKneePowerLossOnReadyCore(); }
        finally { RecLeave(); }
    }

    public void RequestKneePowerLossForwardOnReady()
    {
        var w = rec;
        if (w == null) { RequestKneePowerLossForwardOnReadyCore(); return; }
        using (RecCall(w, nameof(RequestKneePowerLossForwardOnReady))) { }
        recDepth++;
        try { RequestKneePowerLossForwardOnReadyCore(); }
        finally { RecLeave(); }
    }

    public bool BeginKneePowerLossForwardPattern()
    {
        var w = rec;
        if (w == null) return BeginKneePowerLossForwardPatternCore();
        using (RecCall(w, nameof(BeginKneePowerLossForwardPattern))) { }
        recDepth++;
        try { return RecReturn(w, nameof(BeginKneePowerLossForwardPattern), BeginKneePowerLossForwardPatternCore()); }
        finally { RecLeave(); }
    }

    public void StopKneePowerLossPattern()
    {
        var w = rec;
        if (w == null) { StopKneePowerLossPatternCore(); return; }
        using (RecCall(w, nameof(StopKneePowerLossPattern))) { }
        recDepth++;
        try { StopKneePowerLossPatternCore(); }
        finally { RecLeave(); }
    }

    public bool CreateStandingSupport(Vector3 anchorWorldPos, Quaternion uprightRot, string anchorBoneName = "j_kosi")
    {
        var w = rec;
        if (w == null) return CreateStandingSupportCore(anchorWorldPos, uprightRot, anchorBoneName);
        using (RecCall(w, nameof(CreateStandingSupport))) { w.V3(anchorWorldPos); w.Quat(uprightRot); w.Str(anchorBoneName); }
        recDepth++;
        try { return RecReturn(w, nameof(CreateStandingSupport), CreateStandingSupportCore(anchorWorldPos, uprightRot, anchorBoneName)); }
        finally { RecLeave(); }
    }

    public bool UpdateStandingSupport(Vector3 anchorWorldPos, Quaternion uprightRot, string anchorBoneName = "j_kosi")
    {
        var w = rec;
        if (w == null) return UpdateStandingSupportCore(anchorWorldPos, uprightRot, anchorBoneName);
        using (RecCall(w, nameof(UpdateStandingSupport))) { w.V3(anchorWorldPos); w.Quat(uprightRot); w.Str(anchorBoneName); }
        recDepth++;
        try { return RecReturn(w, nameof(UpdateStandingSupport), UpdateStandingSupportCore(anchorWorldPos, uprightRot, anchorBoneName)); }
        finally { RecLeave(); }
    }

    public void RemoveStandingSupport()
    {
        var w = rec;
        if (w == null) { RemoveStandingSupportCore(); return; }
        using (RecCall(w, nameof(RemoveStandingSupport))) { }
        recDepth++;
        try { RemoveStandingSupportCore(); }
        finally { RecLeave(); }
    }

    public bool CreateWristConstraints(Vector3 leftTarget, Vector3 rightTarget,
        float force = 500f, float freq = 80f)
    {
        var w = rec;
        if (w == null) return CreateWristConstraintsCore(leftTarget, rightTarget, force, freq);
        using (RecCall(w, nameof(CreateWristConstraints))) { w.V3(leftTarget); w.V3(rightTarget); w.F32(force); w.F32(freq); }
        recDepth++;
        try { return RecReturn(w, nameof(CreateWristConstraints), CreateWristConstraintsCore(leftTarget, rightTarget, force, freq)); }
        finally { RecLeave(); }
    }

    public bool UpdateWristConstraints(Vector3 leftTarget, Vector3 rightTarget)
    {
        var w = rec;
        if (w == null) return UpdateWristConstraintsCore(leftTarget, rightTarget);
        using (RecCall(w, nameof(UpdateWristConstraints))) { w.V3(leftTarget); w.V3(rightTarget); }
        recDepth++;
        try { return RecReturn(w, nameof(UpdateWristConstraints), UpdateWristConstraintsCore(leftTarget, rightTarget)); }
        finally { RecLeave(); }
    }

    public void RemoveWristConstraints()
    {
        var w = rec;
        if (w == null) { RemoveWristConstraintsCore(); return; }
        using (RecCall(w, nameof(RemoveWristConstraints))) { }
        recDepth++;
        try { RemoveWristConstraintsCore(); }
        finally { RecLeave(); }
    }

    public void ApplyImpulse(string boneName, Vector3 velocityDelta)
    {
        var w = rec;
        if (w == null) { ApplyImpulseCore(boneName, velocityDelta); return; }
        using (RecCall(w, nameof(ApplyImpulse))) { w.Str(boneName); w.V3(velocityDelta); }
        recDepth++;
        try { ApplyImpulseCore(boneName, velocityDelta); }
        finally { RecLeave(); }
    }

    public void ApplyAngularVelocity(string boneName, Vector3 angularVelocityDelta)
    {
        var w = rec;
        if (w == null) { ApplyAngularVelocityCore(boneName, angularVelocityDelta); return; }
        using (RecCall(w, nameof(ApplyAngularVelocity))) { w.Str(boneName); w.V3(angularVelocityDelta); }
        recDepth++;
        try { ApplyAngularVelocityCore(boneName, angularVelocityDelta); }
        finally { RecLeave(); }
    }

    public bool PuntNearest(Vector3 from, float maxDist, float strength, float upBias = 0.5f)
    {
        var w = rec;
        if (w == null) return PuntNearestCore(from, maxDist, strength, upBias);
        using (RecCall(w, nameof(PuntNearest))) { w.V3(from); w.F32(maxDist); w.F32(strength); w.F32(upBias); }
        recDepth++;
        try { return RecReturn(w, nameof(PuntNearest), PuntNearestCore(from, maxDist, strength, upBias)); }
        finally { RecLeave(); }
    }

    public void ClearNpcTraversalSupport(nint npcAddress)
    {
        var w = rec;
        if (w == null) { ClearNpcTraversalSupportCore(npcAddress); return; }
        using (RecCall(w, nameof(ClearNpcTraversalSupport))) w.U64((ulong)npcAddress);
        recDepth++;
        try { ClearNpcTraversalSupportCore(npcAddress); }
        finally { RecLeave(); }
    }

    public bool TryGetNpcTraversalRootHeight(
        nint npcAddress,
        Vector3 proposedRootPosition,
        float terrainY,
        float maxClimb,
        out float rootY,
        int preferredCorpseBodyHandle = -1)
    {
        var w = rec;
        if (w == null)
            return TryGetNpcTraversalRootHeightCore(npcAddress, proposedRootPosition, terrainY, maxClimb, out rootY, preferredCorpseBodyHandle);
        using (RecCall(w, nameof(TryGetNpcTraversalRootHeight)))
        {
            w.U64((ulong)npcAddress); w.V3(proposedRootPosition); w.F32(terrainY); w.F32(maxClimb); w.I32(preferredCorpseBodyHandle);
        }
        recDepth++;
        try
        {
            var ok = TryGetNpcTraversalRootHeightCore(npcAddress, proposedRootPosition, terrainY, maxClimb, out rootY, preferredCorpseBodyHandle);
            using (w.Begin(RecordType.ApiReturn)) { w.Str(nameof(TryGetNpcTraversalRootHeight)); w.Bool(ok); w.F32(rootY); }
            return ok;
        }
        finally { RecLeave(); }
    }

    public void DriveNpcTraversalProxy(
        nint address,
        Vector3 initialRoot,
        Vector3 targetRoot,
        float floorY,
        float visualScale,
        float moveSpeed,
        float maxClimb,
        float stompResponse,
        int concurrentStompers,
        bool locomoting)
    {
        var w = rec;
        if (w == null)
        {
            DriveNpcTraversalProxyCore(address, initialRoot, targetRoot, floorY, visualScale, moveSpeed, maxClimb,
                stompResponse, concurrentStompers, locomoting);
            return;
        }
        using (RecCall(w, nameof(DriveNpcTraversalProxy)))
        {
            w.U64((ulong)address); w.V3(initialRoot); w.V3(targetRoot); w.F32(floorY); w.F32(visualScale); w.F32(moveSpeed);
            w.F32(maxClimb); w.F32(stompResponse); w.I32(concurrentStompers); w.Bool(locomoting);
        }
        recDepth++;
        try
        {
            DriveNpcTraversalProxyCore(address, initialRoot, targetRoot, floorY, visualScale, moveSpeed, maxClimb,
                stompResponse, concurrentStompers, locomoting);
        }
        finally { RecLeave(); }
    }

    public void ReleaseNpcTraversalProxy(nint address)
    {
        var w = rec;
        if (w == null) { ReleaseNpcTraversalProxyCore(address); return; }
        using (RecCall(w, nameof(ReleaseNpcTraversalProxy))) w.U64((ulong)address);
        recDepth++;
        try { ReleaseNpcTraversalProxyCore(address); }
        finally { RecLeave(); }
    }

    public bool AddLiveCollider(nint address)
    {
        var w = rec;
        if (w == null) return AddLiveColliderCore(address);
        using (RecCall(w, nameof(AddLiveCollider))) w.U64((ulong)address);
        recDepth++;
        try { return RecReturn(w, nameof(AddLiveCollider), AddLiveColliderCore(address)); }
        finally { RecLeave(); }
    }

    public void SetLiveColliderLightweight(nint address, bool lightweight)
    {
        var w = rec;
        if (w == null) { SetLiveColliderLightweightCore(address, lightweight); return; }
        using (RecCall(w, nameof(SetLiveColliderLightweight))) { w.U64((ulong)address); w.Bool(lightweight); }
        recDepth++;
        try { SetLiveColliderLightweightCore(address, lightweight); }
        finally { RecLeave(); }
    }

    public void SetLiveColliderTraversalGhost(nint address, bool ghost)
    {
        var w = rec;
        if (w == null) { SetLiveColliderTraversalGhostCore(address, ghost); return; }
        using (RecCall(w, nameof(SetLiveColliderTraversalGhost))) { w.U64((ulong)address); w.Bool(ghost); }
        recDepth++;
        try { SetLiveColliderTraversalGhostCore(address, ghost); }
        finally { RecLeave(); }
    }

    public void RemoveLiveCollider(nint address)
    {
        var w = rec;
        if (w == null) { RemoveLiveColliderCore(address); return; }
        using (RecCall(w, nameof(RemoveLiveCollider))) w.U64((ulong)address);
        recDepth++;
        try { RemoveLiveColliderCore(address); }
        finally { RecLeave(); }
    }

    public void CancelAttackStrike()
    {
        var w = rec;
        if (w == null) { CancelAttackStrikeCore(); return; }
        using (RecCall(w, nameof(CancelAttackStrike))) { }
        recDepth++;
        try { CancelAttackStrikeCore(); }
        finally { RecLeave(); }
    }

    public void ApplyShake(float intensity)
    {
        var w = rec;
        if (w == null) { ApplyShakeCore(intensity); return; }
        using (RecCall(w, nameof(ApplyShake))) w.F32(intensity);
        recDepth++;
        try { ApplyShakeCore(intensity); }
        finally { RecLeave(); }
    }

    internal void AttachWeaponContactBody(BodyHandle weapon)
    {
        var w = rec;
        if (w == null) { AttachWeaponContactBodyCore(weapon); return; }
        using (RecCall(w, nameof(AttachWeaponContactBody))) w.I32(weapon.Value);
        recDepth++;
        try { AttachWeaponContactBodyCore(weapon); }
        finally { RecLeave(); }
    }

    internal void DetachWeaponContactBody(BodyHandle weapon)
    {
        var w = rec;
        if (w == null) { DetachWeaponContactBodyCore(weapon); return; }
        using (RecCall(w, nameof(DetachWeaponContactBody))) w.I32(weapon.Value);
        recDepth++;
        try { DetachWeaponContactBodyCore(weapon); }
        finally { RecLeave(); }
    }

    /// <summary>Recorded since stage A2b: the weapon-drop simulation asks it from outside, and a capture it makes
    /// stays in this controller (its hair can rest on that surface).</summary>
    internal FrozenMeshGeometryCache? GetWeaponContactMesh(nint address)
    {
        var w = rec;
        if (w == null) return GetWeaponContactMeshCore(address);
        using (RecCall(w, nameof(GetWeaponContactMesh))) w.U64((ulong)address);
        recDepth++;
        try
        {
            var cache = GetWeaponContactMeshCore(address);
            using (w.Begin(RecordType.ApiReturn)) { w.Str(nameof(GetWeaponContactMesh)); w.Bool(cache != null); }
            return cache;
        }
        finally { RecLeave(); }
    }
}
