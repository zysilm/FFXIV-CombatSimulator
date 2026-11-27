// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Camera.DeathShots;
using CombatSimulator.Replay.Model;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace CombatSimulator.Replay;

/// <summary>A character the replay records or plays: who it is (address, object index and entity id: the same
/// object only while all three match) and whether it was found alive in the object table on this frame.</summary>
internal sealed class ReplayActor
{
    public nint Address;
    public int ObjectIndex;
    public uint EntityId;
    public bool IsPlayer;
    public string Name = "";
    /// <summary>Found in the object table on this frame's last framework tick (the latest point before the render):
    /// only then is its memory touched in the render hook.</summary>
    public bool Valid;
    /// <summary>Its draw object when it was validated (a redrawn model is a different one).</summary>
    public nint DrawObject;

    /// <summary>
    /// For the render hook: validated on this frame, and the game's object table still holds this object in its slot
    /// with the same draw object (the game's own update runs between the last framework tick and the render, and a
    /// map character can be removed in it). Reads only the table's slot before the object itself.
    /// </summary>
    public unsafe bool StillThere()
    {
        if (!Valid || Address == nint.Zero) return false;
        var manager = GameObjectManager.Instance();
        if (manager == null || ObjectIndex < 0 || ObjectIndex >= manager->Objects.IndexSorted.Length) return false;
        var slot = manager->Objects.IndexSorted[ObjectIndex].Value;
        if ((nint)slot != Address) return false;
        return (nint)slot->DrawObject == DrawObject && DrawObject != nint.Zero;
    }
}

/// <summary>
/// Records the rendered poses of the player and the characters around them into a rolling buffer
/// (docs/DEATH_REPLAY.md §3): 30 samples a second, the last 20 s, one <see cref="PoseTrack"/> per character. Samples
/// are taken in the render hook's read-only observer (<see cref="BoneTransformService.OnPosePrepared"/>: the pose as
/// it is drawn, after the animation, the ragdoll and every other modifier), from characters validated on the same
/// frame. Subscribed only while recording.
/// </summary>
internal sealed unsafe class ReplayRecorder : IDisposable
{
    public const float SampleRate = 30f;
    public const float BufferSeconds = 20f;
    /// <summary>Samples per track: 20 s at 30 a second, and a little room.</summary>
    public const int Capacity = (int)(BufferSeconds * SampleRate) + 16;
    /// <summary>Bones per character at most (a large monster has a few hundred; the rest are not recorded).</summary>
    public const int MaxBones = 256;
    /// <summary>Tracks kept at most: the characters recorded now and some that left the circle (their samples
    /// stay for the replay until the slot is needed).</summary>
    public const int MaxTracks = 24;

    /// <summary>The seven points of the body frame (the death shots' settle detector reads the same bones).</summary>
    private static readonly string[] CoreBones = { "j_kosi", "j_sebo_c", "j_kao", "j_sako_l", "j_sako_r", "j_asi_d_l", "j_asi_d_r" };

    private sealed class Recorded
    {
        public ReplayActor Actor = new();
        public PoseTrack? Track;
        public bool Active;
        public double LastSampled;
        public nint IndexedSkeleton;
        public readonly int[] Core = new int[CoreBones.Length];
    }

    private readonly BoneTransformService bones;
    private readonly Dalamud.Plugin.Services.IPluginLog log;
    private readonly Dictionary<nint, Recorded> tracks = new();
    private readonly List<Recorded> active = new();
    private bool subscribed;
    private double lastSampleAt = double.NegativeInfinity;

    // The player's body coming to rest (docs/DEATH_REPLAY.md §4): fed from the player's samples.
    private readonly SettleDetector settle = new(SettleSpeed, SettleHold);
    private const float SettleSpeed = 0.05f, SettleHold = 0.8f;
    private double lastPlayerSample = double.NaN;
    private readonly Vector3[] corePoints = new Vector3[CoreBones.Length];

    // the 10 s summary
    private long windowSamples, windowTicks, windowMaxTicks, windowBones;

    public ReplayRecorder(BoneTransformService bones, Dalamud.Plugin.Services.IPluginLog log)
    {
        this.bones = bones;
        this.log = log;
    }

    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public bool Recording => subscribed;
    /// <summary>The player's body has kept still for <see cref="SettleHold"/> s (from its recorded points).</summary>
    public bool PlayerSettled => settle.Settled;
    public int ActiveCount => active.Count;

    public void Start()
    {
        if (subscribed) return;
        bones.OnPosePrepared += OnPosePrepared;
        subscribed = true;
        lastSampleAt = double.NegativeInfinity;
    }

    public void Pause()
    {
        if (!subscribed) return;
        bones.OnPosePrepared -= OnPosePrepared;
        subscribed = false;
    }

    /// <summary>Drops every sample (a new simulation, a reset, a zone change: the characters are new).</summary>
    public void Clear()
    {
        foreach (var r in tracks.Values) r.Track?.Clear();
        tracks.Clear();
        active.Clear();
        ResetSettle();
    }

    public void ResetSettle()
    {
        settle.Reset();
        lastPlayerSample = double.NaN;
    }

    /// <summary>
    /// The characters to record from now on (framework tick). A character that is the same object as before keeps
    /// its track; one at a known address that is a different object now starts a new track; the pool is kept to
    /// <see cref="MaxTracks"/> by dropping the tracks not recorded for longest.
    /// </summary>
    public void SetActors(IReadOnlyList<ReplayActor> actors)
    {
        // Mark what stays first, so making room never drops a character that is being recorded.
        foreach (var r in tracks.Values) r.Active = false;
        foreach (var a in actors)
            if (tracks.TryGetValue(a.Address, out var kept)) kept.Active = true;
        active.Clear();
        foreach (var a in actors)
        {
            if (!tracks.TryGetValue(a.Address, out var r))
            {
                EvictFor(1);
                r = new Recorded();
                tracks[a.Address] = r;
            }
            else if (r.Actor.EntityId != a.EntityId || r.Actor.ObjectIndex != a.ObjectIndex)
            {
                r.Track?.Clear();
                r.IndexedSkeleton = nint.Zero;
            }
            r.Actor = a;
            r.Active = true;
            active.Add(r);
        }
    }

    private void EvictFor(int needed)
    {
        while (tracks.Count + needed > MaxTracks)
        {
            nint victim = nint.Zero;
            var oldest = double.PositiveInfinity;
            foreach (var (address, r) in tracks)
            {
                if (r.Active) continue;
                if (r.LastSampled < oldest)
                {
                    oldest = r.LastSampled;
                    victim = address;
                }
            }
            if (victim == nint.Zero) return;
            tracks.Remove(victim);
        }
    }

    /// <summary>The track of a character recorded as this same object, if any.</summary>
    public PoseTrack? TrackOf(ReplayActor actor)
    {
        if (!tracks.TryGetValue(actor.Address, out var r) || r.Track == null || r.Track.Count == 0) return null;
        return r.Actor.EntityId == actor.EntityId && r.Actor.ObjectIndex == actor.ObjectIndex ? r.Track : null;
    }

    /// <summary>The recorded characters with samples (for matching to the live ones at playback).</summary>
    public IEnumerable<(ReplayActor Actor, PoseTrack Track)> Tracks()
    {
        foreach (var r in tracks.Values)
            if (r.Track is { Count: > 0 } t)
                yield return (r.Actor, t);
    }

    public long BufferBytes()
    {
        long sum = 0;
        foreach (var r in tracks.Values)
            if (r.Track != null) sum += r.Track.Bytes;
        return sum;
    }

    /// <summary>The 10 s summary's numbers since the last call: samples taken (frames), the mean and the largest
    /// cost of one sampled frame (ms), bones per sampled frame on average.</summary>
    public (long Frames, double MeanMs, double MaxMs, double MeanBones) TakeWindow()
    {
        var f = windowSamples;
        var mean = f > 0 ? windowTicks * 1000.0 / Stopwatch.Frequency / f : 0;
        var max = windowMaxTicks * 1000.0 / Stopwatch.Frequency;
        var b = f > 0 ? windowBones / (double)f : 0;
        windowSamples = windowTicks = windowMaxTicks = windowBones = 0;
        return (f, mean, max, b);
    }

    private void OnPosePrepared()
    {
        var now = Now;
        // 30 a second, with a little slack so a 60 fps frame is not skipped for being a hair early.
        if (now - lastSampleAt < 1.0 / SampleRate - 0.004) return;
        lastSampleAt = now;
        var t0 = Stopwatch.GetTimestamp();
        long boneCount = 0;
        foreach (var r in active)
        {
            var a = r.Actor;
            if (!a.StillThere()) continue;
            try
            {
                boneCount += SampleOne(r, now);
            }
            catch (Exception ex)
            {
                // (A managed error only; the reads are null-checked.) This character is left out until it is
                // validated again.
                a.Valid = false;
                log.Warning(ex, $"DeathReplay: recording {a.Name} failed; skipped.");
            }
        }
        var ticks = Stopwatch.GetTimestamp() - t0;
        windowSamples++;
        windowTicks += ticks;
        windowMaxTicks = Math.Max(windowMaxTicks, ticks);
        windowBones += boneCount;
    }

    private int SampleOne(Recorded r, double now)
    {
        var a = r.Actor;
        var go = (GameObject*)a.Address;
        if (go->DrawObject == null || (nint)go->DrawObject != a.DrawObject) return 0;
        if (bones.TryGetSkeleton(a.Address) is not { } skel) return 0;
        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return 0;
        var n = Math.Min(skel.BoneCount, MaxBones);
        if (n < 1) return 0;
        if (r.Track == null || r.Track.BoneCount != n)
        {
            // A new character or a redrawn model with another skeleton: a new track (the old samples cannot be
            // played on this skeleton).
            r.Track = new PoseTrack(Capacity, n);
            r.IndexedSkeleton = nint.Zero;
        }

        var draw = (SceneObject*)go->DrawObject;
        var root = new RootPose
        {
            SkeletonPosition = new Vector3(skeleton->Transform.Position.X, skeleton->Transform.Position.Y, skeleton->Transform.Position.Z),
            SkeletonRotation = new Quaternion(skeleton->Transform.Rotation.X, skeleton->Transform.Rotation.Y,
                skeleton->Transform.Rotation.Z, skeleton->Transform.Rotation.W),
            SkeletonScale = new Vector3(skeleton->Transform.Scale.X, skeleton->Transform.Scale.Y, skeleton->Transform.Scale.Z),
            DrawPosition = new Vector3(draw->Position.X, draw->Position.Y, draw->Position.Z),
            DrawRotation = new Quaternion(draw->Rotation.X, draw->Rotation.Y, draw->Rotation.Z, draw->Rotation.W),
        };
        if (!Finite(root.SkeletonPosition) || !Finite(root.DrawPosition)) return 0;
        var slot = r.Track.Append(now, root);
        if (slot < 0) return 0;
        var span = r.Track.BoneSlot(slot);
        var data = skel.Pose->ModelPose.Data;
        for (var i = 0; i < n; i++)
        {
            ref var m = ref data[i];
            PoseQuant.Pack(span.Slice(i * PoseQuant.ShortsPerBone, PoseQuant.ShortsPerBone),
                new Vector3(m.Translation.X, m.Translation.Y, m.Translation.Z),
                new Quaternion(m.Rotation.X, m.Rotation.Y, m.Rotation.Z, m.Rotation.W));
        }
        r.LastSampled = now;
        if (a.IsPlayer) FeedSettle(r, skel, in root, now);
        return n;
    }

    private void FeedSettle(Recorded r, SkeletonAccess skel, in RootPose root, double now)
    {
        if ((nint)skel.HavokSkeleton != r.IndexedSkeleton)
        {
            for (var i = 0; i < CoreBones.Length; i++) r.Core[i] = bones.ResolveBoneIndex(skel, CoreBones[i]);
            r.IndexedSkeleton = (nint)skel.HavokSkeleton;
        }
        var data = skel.Pose->ModelPose.Data;
        for (var i = 0; i < CoreBones.Length; i++)
        {
            var idx = r.Core[i];
            if (idx < 0 || idx >= skel.BoneCount) return;
            ref var m = ref data[idx];
            corePoints[i] = root.ModelToWorld(new Vector3(m.Translation.X, m.Translation.Y, m.Translation.Z));
        }
        var points = new BodyPoints(corePoints[0], corePoints[1], corePoints[2], corePoints[3], corePoints[4], corePoints[5], corePoints[6]);
        var dt = double.IsNaN(lastPlayerSample) ? 0f : (float)Math.Clamp(now - lastPlayerSample, 0.0, 0.5);
        lastPlayerSample = now;
        settle.Update(in points, dt);
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    public void Dispose()
    {
        Pause();
        tracks.Clear();
        active.Clear();
    }
}
