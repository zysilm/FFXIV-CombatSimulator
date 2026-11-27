// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Replay.Model;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace CombatSimulator.Replay;

/// <summary>
/// Plays recorded poses back onto the characters (docs/DEATH_REPLAY.md §5). Render state only, like the ragdoll:
/// <list type="bullet">
/// <item>the main skeleton's model-space pose, written in the render hook's last modifier pass
/// (<see cref="BoneTransformService.OnFinalPoseModifiers"/>, subscribed when playback starts so it runs after every
/// other modifier, the ragdoll's included). The recorded world pose is converted to the skeleton's transform of
/// this frame, so the bones are where they were whatever the character's root does now. The partial skeletons on a
/// body bone (face, hair) are carried rigidly with it;</item>
/// <item>the draw object's position and rotation (what the game culls by), in a framework tick that runs after the
/// plugin's and every other tick that moves characters.</item>
/// </list>
/// Nothing else: no game object position, no animation state, no packet. At the end the draw objects go back to
/// where their game objects are now (with the offset they had), and a pose nobody has written since (a character
/// whose animation does not refresh its pose) gets the one it had when the replay started; the others (a ragdoll,
/// a running animation) refresh themselves on the next frame.
/// </summary>
internal sealed unsafe class ReplayPosePlayer : IDisposable
{
    private struct BoneSnap
    {
        public Vector3 T;
        public Quaternion R;
    }

    private sealed class PartSnap
    {
        public int Partial;
        public BoneSnap[] Bones = Array.Empty<BoneSnap>();
    }

    private sealed class Played
    {
        public ReplayActor Actor = null!;
        public PoseTrack Track = null!;
        public Vector3[] Pos = Array.Empty<Vector3>();
        public Quaternion[] Rot = Array.Empty<Quaternion>();
        public string? Problem;

        // the pose as it was when the replay started (first render frame)
        public bool Snapped;
        public BoneSnap[] Body = Array.Empty<BoneSnap>();
        public readonly List<PartSnap> Parts = new();
        // a few bones of what was written last, to see at the end whether anyone has written the pose since
        public bool Wrote;
        public int Mid, Last;
        public Vector3 W0, WMid, WLast;

        // partial skeletons on a body bone: partial index → body bone (-1: not on one)
        public int PartialCount = -1;
        public int[] PartialRoot = Array.Empty<int>();
        public Vector3[] BeforeT = Array.Empty<Vector3>();
        public Quaternion[] BeforeR = Array.Empty<Quaternion>();

        // the draw object as it was when the replay started (first framework write)
        public bool DrawSnapped;
        public Vector3 DrawPos0, GoPos0;
        public Quaternion DrawRot0;
        public float GoYaw0;
    }

    private readonly BoneTransformService bones;
    private readonly Dalamud.Plugin.Services.IPluginLog log;
    private readonly List<Played> played = new();
    private bool subscribed, playing, restoreRoots, restorePoses;
    private double time;

    // costs, for the end line
    private long writeFrames, writeTicks, writeMaxTicks;

    public ReplayPosePlayer(BoneTransformService bones, Dalamud.Plugin.Services.IPluginLog log)
    {
        this.bones = bones;
        this.log = log;
    }

    public bool Playing => playing;
    /// <summary>Still putting the characters back (the draw objects on the next framework tick, the poses on the
    /// next render).</summary>
    public bool Restoring => restoreRoots || restorePoses;
    public int Count => played.Count;

    /// <summary>Starts writing these characters' recorded poses (framework tick). <see cref="SetTime"/> says which
    /// moment.</summary>
    public void Begin(IReadOnlyList<(ReplayActor Actor, PoseTrack Track)> actors, double startTime)
    {
        FinishNow();
        played.Clear();
        foreach (var (actor, track) in actors)
        {
            played.Add(new Played
            {
                Actor = actor,
                Track = track,
                Pos = new Vector3[track.BoneCount],
                Rot = new Quaternion[track.BoneCount],
            });
        }
        time = startTime;
        playing = true;
        restoreRoots = restorePoses = false;
        writeFrames = writeTicks = writeMaxTicks = 0;
        Subscribe();
    }

    public void SetTime(double recordedTime) => time = recordedTime;

    /// <summary>Stops writing; the characters are put back on the next framework tick and render (see the class).
    /// Touches no game memory itself, so it can be called from anywhere (a reset from the window).</summary>
    public void End()
    {
        if (!playing) return;
        playing = false;
        restoreRoots = true;
        restorePoses = true;
    }

    /// <summary>Drops everything without touching the game (logout: the characters are gone).</summary>
    public void Abandon()
    {
        playing = restoreRoots = restorePoses = false;
        played.Clear();
        Unsubscribe();
    }

    public (double MeanMs, double MaxMs, long Frames) Costs() =>
        (writeFrames > 0 ? writeTicks * 1000.0 / Stopwatch.Frequency / writeFrames : 0,
            writeMaxTicks * 1000.0 / Stopwatch.Frequency, writeFrames);

    /// <summary>The characters played (for validation each framework tick).</summary>
    public IEnumerable<ReplayActor> Actors()
    {
        foreach (var p in played) yield return p.Actor;
    }

    public int SkippedCount()
    {
        var n = 0;
        foreach (var p in played)
            if (p.Problem != null || !p.Actor.Valid) n++;
        return n;
    }

    private void Subscribe()
    {
        if (subscribed) return;
        // Subscribed now, after everything that subscribed at load: this pass runs after every other modifier.
        bones.OnFinalPoseModifiers += OnFinalPoseModifiers;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed) return;
        bones.OnFinalPoseModifiers -= OnFinalPoseModifiers;
        subscribed = false;
    }

    // ------------------------------------------------------------------
    // Framework tick (late): the draw objects
    // ------------------------------------------------------------------

    /// <summary>The late framework tick, after the characters were validated: writes the draw objects of this
    /// moment, or puts them back after the end.</summary>
    public void TickRoots()
    {
        if (playing)
        {
            foreach (var p in played)
            {
                if (!p.Actor.Valid || p.Problem != null) continue;
                try { WriteRoot(p); }
                catch (Exception ex)
                {
                    p.Problem = "error";
                    log.Warning(ex, $"DeathReplay: moving {p.Actor.Name}'s model failed; it is left as it is.");
                }
            }
        }
        else if (restoreRoots)
        {
            restoreRoots = false;
            foreach (var p in played)
            {
                if (!p.Actor.Valid || !p.DrawSnapped) continue;
                try { RestoreRoot(p); }
                catch (Exception ex) { log.Warning(ex, $"DeathReplay: putting {p.Actor.Name}'s model back failed."); }
            }
            if (!restorePoses) FinishNow();
        }
    }

    private void WriteRoot(Played p)
    {
        var go = (GameObject*)p.Actor.Address;
        var draw = go->DrawObject;
        if (draw == null) return;
        var obj = (SceneObject*)draw;
        if (!p.DrawSnapped)
        {
            p.DrawPos0 = new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z);
            p.DrawRot0 = new Quaternion(obj->Rotation.X, obj->Rotation.Y, obj->Rotation.Z, obj->Rotation.W);
            p.GoPos0 = new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
            p.GoYaw0 = go->Rotation;
            p.DrawSnapped = true;
        }
        if (!p.Track.SampleRoot(time, out var root)) return;
        if (!Finite(root.DrawPosition) || !Finite(root.DrawRotation)) return;
        obj->Position = root.DrawPosition;
        obj->Rotation = Quaternion.Normalize(root.DrawRotation);
        draw->NotifyTransformChanged();
    }

    /// <summary>Back to where the game object is now, with the offset and turn the draw object had from it when the
    /// replay started (a character that moved meanwhile is drawn where it is; one that did not, exactly as before).</summary>
    private void RestoreRoot(Played p)
    {
        var go = (GameObject*)p.Actor.Address;
        var draw = go->DrawObject;
        if (draw == null) return;
        var obj = (SceneObject*)draw;
        var goPos = new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
        var turn = ReplayMath.WrapAngle(go->Rotation - p.GoYaw0);
        var spin = Quaternion.CreateFromAxisAngle(Vector3.UnitY, turn);
        var position = goPos + Vector3.Transform(p.DrawPos0 - p.GoPos0, spin);
        var rotation = Quaternion.Normalize(spin * p.DrawRot0);
        if (!Finite(position) || !Finite(rotation)) return;
        obj->Position = position;
        obj->Rotation = rotation;
        draw->NotifyTransformChanged();
    }

    // ------------------------------------------------------------------
    // Render hook: the poses
    // ------------------------------------------------------------------

    private void OnFinalPoseModifiers()
    {
        if (playing)
        {
            var t0 = Stopwatch.GetTimestamp();
            foreach (var p in played)
            {
                if (!p.Actor.StillThere() || p.Problem != null) continue;
                try { WritePose(p); }
                catch (Exception ex)
                {
                    p.Problem = "error";
                    log.Warning(ex, $"DeathReplay: writing {p.Actor.Name}'s pose failed; it is left as it is.");
                }
            }
            var ticks = Stopwatch.GetTimestamp() - t0;
            writeFrames++;
            writeTicks += ticks;
            writeMaxTicks = Math.Max(writeMaxTicks, ticks);
        }
        else if (restorePoses && !restoreRoots)
        {
            // (After the draw objects went back on this frame's framework tick.)
            restorePoses = false;
            foreach (var p in played)
            {
                if (!p.Actor.StillThere() || !p.Snapped) continue;
                try { RestorePose(p); }
                catch (Exception ex) { log.Warning(ex, $"DeathReplay: putting {p.Actor.Name}'s pose back failed."); }
            }
            FinishNow();
        }
    }

    private void WritePose(Played p)
    {
        var go = (GameObject*)p.Actor.Address;
        if (go->DrawObject == null || (nint)go->DrawObject != p.Actor.DrawObject) return;
        if (bones.TryGetSkeleton(p.Actor.Address, synchronizePose: true) is not { } skel) return;
        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return;
        var n = Math.Min(skel.BoneCount, ReplayRecorder.MaxBones);
        if (n != p.Track.BoneCount)
        {
            p.Problem = "its skeleton is not the recorded one";
            log.Info($"DeathReplay: {p.Actor.Name} skipped — {p.Problem} ({skel.BoneCount} bones, recorded {p.Track.BoneCount}).");
            return;
        }
        if (!p.Track.Sample(time, p.Pos, p.Rot, out var root)) return;

        var data = skel.Pose->ModelPose.Data;
        if (!p.Snapped) Snapshot(p, skel);

        // The recorded world pose in this frame's skeleton transform.
        var curPos = new Vector3(skeleton->Transform.Position.X, skeleton->Transform.Position.Y, skeleton->Transform.Position.Z);
        var curRot = new Quaternion(skeleton->Transform.Rotation.X, skeleton->Transform.Rotation.Y,
            skeleton->Transform.Rotation.Z, skeleton->Transform.Rotation.W);
        var curScale = new Vector3(skeleton->Transform.Scale.X, skeleton->Transform.Scale.Y, skeleton->Transform.Scale.Z);
        var same = Vector3.DistanceSquared(curPos, root.SkeletonPosition) < 1e-10f &&
                   MathF.Abs(Quaternion.Dot(curRot, root.SkeletonRotation)) > 0.9999999f &&
                   Vector3.DistanceSquared(curScale, root.SkeletonScale) < 1e-10f;
        var curInv = Quaternion.Inverse(Quaternion.Normalize(curRot));
        var turn = Quaternion.Normalize(curInv * root.SkeletonRotation);
        if (!Finite(curScale) || MathF.Abs(curScale.X) < 1e-4f || MathF.Abs(curScale.Y) < 1e-4f || MathF.Abs(curScale.Z) < 1e-4f)
            same = true; // (a collapsed root cannot be divided by; write the model pose as recorded)

        // The partial skeletons' body bones as they are before this write.
        EnsurePartials(p, skel);
        for (var j = 0; j < p.PartialRoot.Length; j++)
        {
            var b = p.PartialRoot[j];
            if (b < 0 || b >= n) continue;
            ref var m = ref data[b];
            p.BeforeT[j] = new Vector3(m.Translation.X, m.Translation.Y, m.Translation.Z);
            p.BeforeR[j] = new Quaternion(m.Rotation.X, m.Rotation.Y, m.Rotation.Z, m.Rotation.W);
        }

        for (var i = 0; i < n; i++)
        {
            var t = p.Pos[i];
            var r = p.Rot[i];
            if (!same)
            {
                var world = root.ModelToWorld(t);
                t = Vector3.Transform(world - curPos, curInv) / curScale;
                r = Quaternion.Normalize(turn * r);
            }
            if (!Finite(t) || !Finite(r)) continue;
            ref var m = ref data[i];
            m.Translation.X = t.X;
            m.Translation.Y = t.Y;
            m.Translation.Z = t.Z;
            m.Rotation.X = r.X;
            m.Rotation.Y = r.Y;
            m.Rotation.Z = r.Z;
            m.Rotation.W = r.W;
        }

        // Face, hair, ...: carried rigidly with their body bone.
        for (var j = 0; j < p.PartialRoot.Length; j++)
        {
            var b = p.PartialRoot[j];
            if (b < 0 || b >= n) continue;
            ref var after = ref data[b];
            var afterT = new Vector3(after.Translation.X, after.Translation.Y, after.Translation.Z);
            var afterR = new Quaternion(after.Rotation.X, after.Rotation.Y, after.Rotation.Z, after.Rotation.W);
            var delta = Quaternion.Normalize(afterR * Quaternion.Inverse(Quaternion.Normalize(p.BeforeR[j])));
            CarryPartial(skel, j + 1, p.BeforeT[j], afterT, delta);
        }

        p.Mid = n / 2;
        p.Last = n - 1;
        p.W0 = T(ref data[0]);
        p.WMid = T(ref data[p.Mid]);
        p.WLast = T(ref data[p.Last]);
        p.Wrote = true;
    }

    private static Vector3 T(ref FFXIVClientStructs.Havok.Common.Base.Math.QsTransform.hkQsTransformf m) =>
        new(m.Translation.X, m.Translation.Y, m.Translation.Z);

    private static void CarryPartial(SkeletonAccess skel, int partial, Vector3 beforeT, Vector3 afterT, Quaternion delta)
    {
        var skeleton = skel.CharBase->Skeleton;
        if (partial >= skeleton->PartialSkeletonCount) return;
        var pose = skeleton->PartialSkeletons[partial].GetHavokPose(0);
        if (pose == null || pose->Skeleton == null || pose->ModelInSync == 0) return;
        var count = pose->ModelPose.Length;
        for (var b = 0; b < count; b++)
        {
            ref var m = ref pose->ModelPose.Data[b];
            var oldT = new Vector3(m.Translation.X, m.Translation.Y, m.Translation.Z);
            var oldR = new Quaternion(m.Rotation.X, m.Rotation.Y, m.Rotation.Z, m.Rotation.W);
            var newT = afterT + Vector3.Transform(oldT - beforeT, delta);
            var newR = Quaternion.Normalize(delta * oldR);
            if (!Finite(newT) || !Finite(newR)) continue;
            m.Translation.X = newT.X;
            m.Translation.Y = newT.Y;
            m.Translation.Z = newT.Z;
            m.Rotation.X = newR.X;
            m.Rotation.Y = newR.Y;
            m.Rotation.Z = newR.Z;
            m.Rotation.W = newR.W;
        }
    }

    private void EnsurePartials(Played p, SkeletonAccess skel)
    {
        var skeleton = skel.CharBase->Skeleton;
        var count = skeleton->PartialSkeletonCount;
        if (count == p.PartialCount) return;
        p.PartialCount = count;
        var others = Math.Max(0, count - 1);
        p.PartialRoot = new int[others];
        p.BeforeT = new Vector3[others];
        p.BeforeR = new Quaternion[others];
        for (var j = 0; j < others; j++)
        {
            p.PartialRoot[j] = -1;
            var pose = skeleton->PartialSkeletons[j + 1].GetHavokPose(0);
            if (pose == null || pose->Skeleton == null || pose->Skeleton->Bones.Length < 1) continue;
            var rootName = pose->Skeleton->Bones[0].Name.String;
            if (string.IsNullOrEmpty(rootName)) continue;
            p.PartialRoot[j] = bones.ResolveBoneIndex(skel, rootName);
        }
    }

    private static void Snapshot(Played p, SkeletonAccess skel)
    {
        var data = skel.Pose->ModelPose.Data;
        p.Body = new BoneSnap[skel.BoneCount];
        for (var i = 0; i < skel.BoneCount; i++)
        {
            ref var m = ref data[i];
            p.Body[i] = new BoneSnap
            {
                T = new Vector3(m.Translation.X, m.Translation.Y, m.Translation.Z),
                R = new Quaternion(m.Rotation.X, m.Rotation.Y, m.Rotation.Z, m.Rotation.W),
            };
        }
        p.Parts.Clear();
        var skeleton = skel.CharBase->Skeleton;
        for (var ps = 1; ps < skeleton->PartialSkeletonCount; ps++)
        {
            var pose = skeleton->PartialSkeletons[ps].GetHavokPose(0);
            if (pose == null || pose->Skeleton == null || pose->ModelInSync == 0) continue;
            var part = new PartSnap { Partial = ps, Bones = new BoneSnap[pose->ModelPose.Length] };
            for (var b = 0; b < part.Bones.Length; b++)
            {
                ref var m = ref pose->ModelPose.Data[b];
                part.Bones[b] = new BoneSnap
                {
                    T = new Vector3(m.Translation.X, m.Translation.Y, m.Translation.Z),
                    R = new Quaternion(m.Rotation.X, m.Rotation.Y, m.Rotation.Z, m.Rotation.W),
                };
            }
            p.Parts.Add(part);
        }
        p.Snapped = true;
    }

    /// <summary>The pose as it was at the start, but only where nobody has written it since the replay's last
    /// write (the same values are still there): anything that refreshes the pose itself is left to do so.</summary>
    private void RestorePose(Played p)
    {
        if (!p.Wrote) return;
        var go = (GameObject*)p.Actor.Address;
        if (go->DrawObject == null || (nint)go->DrawObject != p.Actor.DrawObject) return;
        if (bones.TryGetSkeleton(p.Actor.Address) is not { } skel) return;
        if (skel.BoneCount != p.Body.Length || p.Last >= skel.BoneCount) return;
        var data = skel.Pose->ModelPose.Data;
        if (T(ref data[0]) != p.W0 || T(ref data[p.Mid]) != p.WMid || T(ref data[p.Last]) != p.WLast)
            return; // refreshed by the game or the ragdoll since: it is live already
        for (var i = 0; i < p.Body.Length; i++)
        {
            ref var m = ref data[i];
            var s = p.Body[i];
            m.Translation.X = s.T.X;
            m.Translation.Y = s.T.Y;
            m.Translation.Z = s.T.Z;
            m.Rotation.X = s.R.X;
            m.Rotation.Y = s.R.Y;
            m.Rotation.Z = s.R.Z;
            m.Rotation.W = s.R.W;
        }
        var skeleton = skel.CharBase->Skeleton;
        foreach (var part in p.Parts)
        {
            if (part.Partial >= skeleton->PartialSkeletonCount) continue;
            var pose = skeleton->PartialSkeletons[part.Partial].GetHavokPose(0);
            if (pose == null || pose->ModelPose.Length != part.Bones.Length) continue;
            for (var b = 0; b < part.Bones.Length; b++)
            {
                ref var m = ref pose->ModelPose.Data[b];
                var s = part.Bones[b];
                m.Translation.X = s.T.X;
                m.Translation.Y = s.T.Y;
                m.Translation.Z = s.T.Z;
                m.Rotation.X = s.R.X;
                m.Rotation.Y = s.R.Y;
                m.Rotation.Z = s.R.Z;
                m.Rotation.W = s.R.W;
            }
        }
        log.Debug($"DeathReplay: {p.Actor.Name}'s pose had not been refreshed since the replay; put back as it was.");
    }

    /// <summary>Puts everything back at once, outside the frame (plugin unload; the characters were validated just
    /// before). Pose and draw object as in the ordinary end.</summary>
    public void RestoreImmediately()
    {
        if (!playing && !Restoring) return;
        playing = false;
        foreach (var p in played)
        {
            if (!p.Actor.Valid) continue;
            try
            {
                if (p.DrawSnapped) RestoreRoot(p);
                if (p.Snapped) RestorePose(p);
            }
            catch (Exception ex) { log.Warning(ex, $"DeathReplay: putting {p.Actor.Name} back failed."); }
        }
        FinishNow();
    }

    private void FinishNow()
    {
        playing = restoreRoots = restorePoses = false;
        played.Clear();
        Unsubscribe();
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);

    public void Dispose() => Abandon();
}
