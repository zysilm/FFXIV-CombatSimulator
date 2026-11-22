// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using AdvancedRagdoll.Recording;

namespace RagdollReplay;

/// <summary>
/// Read-only export of pose trajectories for the death-shot camera's offline check (docs/DEATH_SHOTS.md):
///   RagdollReplay &lt;file.rgdrec | folder&gt; --export-trajectories [outFolder]
/// Nothing is replayed. For every render frame of a recording it writes the time, whether physics had started,
/// and the world positions of a fixed list of bones: before physics from the frame's SkeletonPose (PreFrame)
/// record, after it from the frame's PoseOut record, plus the centre of each bone's rigid body from the frame's
/// last BodyStates record (what the plugin reads through Ragdoll.GetBodyWorldPosition). It also writes the ground
/// the recording knows: every ground ray hit, the exact ground triangles where recorded, and the water surface.
/// The output (one small .dstraj file per recording, default %TEMP%\DeathShots\traj) is derived from the user's
/// own recordings and never goes into the repository. Format: see <see cref="Write"/>; reader in
/// Tests/DeathShots/Trajectory.cs.
/// </summary>
internal static class TrajectoryExport
{
    /// <summary>The bones exported, in file order. The first seven are what the body frame needs; the rest
    /// fill in the head-to-feet chain and the limbs for "whole body" framing.</summary>
    public static readonly string[] Bones =
    {
        "j_kosi", "j_sebo_c", "j_kao", "j_sako_l", "j_sako_r", "j_asi_d_l", "j_asi_d_r",
        "j_sebo_a", "j_sebo_b", "j_kubi",
        "j_ude_a_l", "j_ude_a_r", "j_ude_b_l", "j_ude_b_r", "j_te_l", "j_te_r",
        "j_asi_a_l", "j_asi_a_r", "j_asi_b_l", "j_asi_b_r", "j_asi_c_l", "j_asi_c_r",
    };

    private const int RequiredBones = 7;

    private sealed class Frame
    {
        public float Time, Dt, GroundY;
        public bool PhysicsStarted, PoseFresh;
        public byte PoseSource; // 0 none, 1 SkeletonPose PreFrame, 2 PoseOut, 3 SkeletonPose Init/FrameIn (no PoseOut in the frame)
        public Vector3[] Bone = Array.Empty<Vector3>();
        public Vector3[] Body = Array.Empty<Vector3>();
    }

    public static int Run(string target, string? outFolder)
    {
        outFolder ??= Path.Combine(Path.GetTempPath(), "DeathShots", "traj");
        Directory.CreateDirectory(outFolder);
        var files = Directory.Exists(target)
            ? Directory.GetFiles(target, "*" + RagdollRecordingFormat.FileExtension).OrderBy(p => p, StringComparer.Ordinal).ToArray()
            : new[] { target };
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine("file | who | kind | frames | seconds | pre-physics frames (with fresh pose / repeated / none) | physics frames (PoseOut / bodies) | physics start s | ground rays | ground tris | water");
        int players = 0, npcs = 0, humanoidNpcs = 0, humanoidPlayers = 0, failed = 0;
        long preFrames = 0, preWithPose = 0, physFrames = 0, physWithPose = 0, physWithBodies = 0;
        foreach (var file in files)
        {
            try
            {
                var s = Export(file, outFolder);
                if (s.IsPlayer) { players++; if (s.Humanoid) humanoidPlayers++; }
                else { npcs++; if (s.Humanoid) humanoidNpcs++; }
                preFrames += s.Pre; preWithPose += s.PreFresh + s.PreRepeated; physFrames += s.Phys; physWithPose += s.PhysPose; physWithBodies += s.PhysBodies;
                Console.WriteLine(string.Create(inv,
                    $"{Path.GetFileName(file)} | {(s.IsPlayer ? "player" : "npc")} | {(s.Humanoid ? "humanoid" : "other(" + s.MissingBones + ")")} | {s.Frames} | {s.Seconds:F1} | {s.Pre} ({s.PreFresh}/{s.PreRepeated}/{s.Pre - s.PreFresh - s.PreRepeated}) | {s.Phys} ({s.PhysPose}/{s.PhysBodies}) | {(s.PhysicsStart < 0 ? "never" : s.PhysicsStart.ToString("F2", inv))} | {s.GroundRays} | {s.GroundTriangles} | {(float.IsNaN(s.Water) ? "-" : s.Water.ToString("F2", inv))}{(s.Note.Length > 0 ? " | " + s.Note : "")}"));
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"{Path.GetFileName(file)} | EXPORT ERROR: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.WriteLine(string.Create(inv,
            $"== {files.Length - failed} of {files.Length} exported to {outFolder}: {players} player ({humanoidPlayers} humanoid), {npcs} npc ({humanoidNpcs} humanoid); " +
            $"pre-physics frames {preFrames}, with a pose {preWithPose}; physics frames {physFrames}, with PoseOut {physWithPose}, with body states {physWithBodies}."));
        return failed == 0 ? 0 : 1;
    }

    private sealed class Stats
    {
        public bool IsPlayer, Humanoid;
        public string MissingBones = "", Note = "";
        public int Frames, Pre, PreFresh, PreRepeated, Phys, PhysPose, PhysBodies, GroundRays, GroundTriangles;
        public float Seconds, PhysicsStart = -1f, Water = float.NaN;
    }

    /// <summary>The trajectory of one recording under another name (the what-if runs: the recording is a temporary file).</summary>
    internal static void ExportAs(string path, string outFolder, string exportName) => Export(path, outFolder, exportName);

    private static Stats Export(string path, string outFolder, string? exportName = null)
    {
        var stats = new Stats();
        var frames = new List<Frame>();
        var groundRays = new List<Vector3>();
        var triangles = new List<Vector3>();
        var triangleBoxes = new HashSet<(Vector3, Vector3)>();
        ulong actor = 0;
        var haveActor = false;
        float facing = 0f, groundY = float.NaN, activateGroundY = float.NaN;
        var activatePosition = Vector3.Zero;
        int[] boneIndex = Enumerable.Repeat(-1, Bones.Length).ToArray();
        var haveTopology = false;
        var bodyOfHandle = new Dictionary<int, int>(); // body handle -> index into Bones
        var lastPose = new Dictionary<int, byte[]>();   // purpose -> partial 0 bytes
        Frame? frame = null;
        using var reader = RagdollRecordingReader.Open(path);
        foreach (var r in reader.ReadAll())
        {
            switch (r.Type)
            {
                case RecordType.Header:
                    stats.IsPlayer = r[21].AsBool;
                    break;
                case RecordType.Activate:
                    actor = r[0].AsULong;
                    haveActor = true;
                    activatePosition = r[4].AsVector3;
                    activateGroundY = groundY = activatePosition.Y;
                    break;
                case RecordType.Identity:
                    if (haveActor && r[0].AsULong == actor) facing = r[5].AsFloat;
                    break;
                case RecordType.SkeletonTopology:
                    if (haveActor && r[0].AsULong == actor && r[1].AsInt == 0)
                    {
                        var names = r[6].Count;
                        Array.Fill(boneIndex, -1);
                        for (var i = 0; i < names; i++)
                        {
                            var name = r[7 + i].AsString;
                            var at = name == null ? -1 : Array.IndexOf(Bones, name);
                            if (at >= 0 && boneIndex[at] < 0) boneIndex[at] = i;
                        }
                        haveTopology = true;
                    }
                    break;
                case RecordType.RenderFrame:
                    frame = new Frame
                    {
                        Dt = r[1].AsFloat, Time = r[2].AsFloat, PhysicsStarted = r[3].AsBool, GroundY = groundY,
                        Bone = Nan(), Body = Nan(),
                    };
                    frames.Add(frame);
                    break;
                case RecordType.SkeletonPose:
                case RecordType.PoseOut:
                {
                    if (frame == null || !haveActor || r[0].AsULong != actor || !haveTopology) break;
                    var purpose = r[1].AsInt;
                    var isOut = r.Type == RecordType.PoseOut;
                    if (!isOut && purpose is not ((int)PosePurpose.PreFrame or (int)PosePurpose.Init or (int)PosePurpose.FrameIn)) break;
                    if (!r[2].AsBool) break;
                    // A pose after the frame's write-back wins over the ones read before it.
                    if (frame.PoseSource == 2 && !isOut) break;
                    var bytes = PartialZero(r, purpose, lastPose, out var fresh);
                    if (bytes == null) break;
                    var position = r[3].AsVector3;
                    var rotation = r[4].AsQuaternion;
                    var scale = r[5].AsVector3;
                    var floats = MemoryMarshal.Cast<byte, float>(bytes);
                    for (var b = 0; b < Bones.Length; b++)
                    {
                        var i = boneIndex[b];
                        if (i < 0 || (i + 1) * 12 > floats.Length) continue;
                        var local = new Vector3(floats[i * 12], floats[i * 12 + 1], floats[i * 12 + 2]);
                        frame.Bone[b] = position + Vector3.Transform(local * scale, rotation);
                    }
                    frame.PoseSource = isOut ? (byte)2 : purpose == (int)PosePurpose.PreFrame ? (byte)1 : (byte)3;
                    frame.PoseFresh = fresh;
                    break;
                }
                case RecordType.RigBodies:
                    bodyOfHandle.Clear();
                    try
                    {
                        var count = r[0].AsInt;
                        var at = 1;
                        for (var i = 0; i < count; i++)
                        {
                            if (r[at].Tag != ValueTag.String || r[at + 2].Tag != ValueTag.I32 || r[at + 3].Tag != ValueTag.List)
                                throw new InvalidDataException("unexpected RigBodies layout");
                            var b = Array.IndexOf(Bones, r[at].AsString);
                            if (b >= 0) bodyOfHandle[r[at + 2].AsInt] = b;
                            at += 4 + r[at + 3].Count;
                        }
                    }
                    catch (Exception ex)
                    {
                        bodyOfHandle.Clear();
                        stats.Note = "bodies not mapped: " + ex.Message;
                    }
                    break;
                case RecordType.InitEnd:
                    // realGroundY is the ray hit under the body; groundY is what the simulation used.
                    groundY = float.IsFinite(r[3].AsFloat) ? r[3].AsFloat : r[2].AsFloat;
                    if (frame != null) frame.GroundY = groundY;
                    break;
                case RecordType.BodyStates:
                {
                    if (frame == null || bodyOfHandle.Count == 0) break;
                    var handles = r[4];
                    var state = r[6];
                    for (var i = 0; i < handles.Count; i++)
                    {
                        if (!bodyOfHandle.TryGetValue(handles.IntAt(i), out var b)) continue;
                        var at = i * RagdollRecordingFormat.BodyStateFloats;
                        frame.Body[b] = new Vector3(state.FloatAt(at), state.FloatAt(at + 1), state.FloatAt(at + 2));
                    }
                    break;
                }
                case RecordType.Raycast:
                    if (r[4].AsBool && r[2].AsVector3.Y < -0.9f) groundRays.Add(r[5].AsVector3);
                    break;
                case RecordType.GroundTriangles:
                    if (r[3].AsBool && triangleBoxes.Add((r[0].AsVector3, r[1].AsVector3)))
                        triangles.AddRange(Rec.Vectors(r[4]));
                    break;
                case RecordType.WaterSurface:
                    if (r[3].AsBool) stats.Water = r[4].AsFloat;
                    break;
            }
        }

        var missing = new List<string>();
        for (var b = 0; b < RequiredBones; b++)
            if (boneIndex[b] < 0) missing.Add(Bones[b]);
        stats.Humanoid = haveTopology && missing.Count == 0;
        stats.MissingBones = haveTopology ? string.Join(",", missing) : "no skeleton";
        stats.Frames = frames.Count;
        stats.Seconds = frames.Count > 0 ? frames[^1].Time : 0f;
        stats.GroundRays = groundRays.Count;
        stats.GroundTriangles = triangles.Count / 3;
        foreach (var f in frames)
        {
            var bodies = f.Body.Any(v => !float.IsNaN(v.X));
            if (!f.PhysicsStarted)
            {
                stats.Pre++;
                if (f.PoseSource != 0) { if (f.PoseFresh) stats.PreFresh++; else stats.PreRepeated++; }
            }
            else
            {
                if (stats.PhysicsStart < 0) stats.PhysicsStart = f.Time;
                stats.Phys++;
                if (f.PoseSource == 2) stats.PhysPose++;
                if (bodies) stats.PhysBodies++;
            }
        }

        exportName ??= Path.GetFileNameWithoutExtension(path);
        Write(Path.Combine(outFolder, exportName + ".dstraj"),
            exportName, stats, facing, activatePosition, activateGroundY, groundRays, triangles, frames);
        return stats;
    }

    private static Vector3[] Nan()
    {
        var a = new Vector3[Bones.Length];
        Array.Fill(a, new Vector3(float.NaN));
        return a;
    }

    /// <summary>The model pose of partial skeleton 0 in a pose record, following "same as the previous record of
    /// this purpose".</summary>
    private static byte[]? PartialZero(RagdollRecord pose, int purpose, Dictionary<int, byte[]> last, out bool fresh)
    {
        fresh = false;
        var key = pose.Type == RecordType.PoseOut ? 1000 + purpose : purpose;
        var partials = pose[8].AsInt;
        var at = 9;
        for (var p = 0; p < partials; p++)
        {
            var index = pose[at].AsInt;
            var state = pose[at + 1].AsInt & 3;
            at += 2;
            byte[]? bytes = null;
            if (state == 0) { bytes = pose[at].Data; at++; }
            if (index != 0) continue;
            if (state == 0) { last[key] = bytes!; fresh = true; return bytes; }
            if (state == 1) return last.GetValueOrDefault(key);
            return null;
        }
        return null;
    }

    /// <summary>
    /// File layout (little-endian): "DSTRAJ01"; name:str; isPlayer:u8; humanoid:u8; facing:f32; activatePosition:3f;
    /// activateGroundY:f32; water:f32 (NaN = none); boneCount:i32, names:str each; groundRayCount:i32, points:3f each;
    /// triangleVertexCount:i32, vertices:3f each (3 per triangle); frameCount:i32; then per frame: time:f32, dt:f32,
    /// physicsStarted:u8, poseSource:u8 (0 none, 1 SkeletonPose PreFrame, 2 PoseOut, 3 SkeletonPose Init/FrameIn),
    /// poseFresh:u8, groundY:f32, bone:3f × boneCount (NaN = not available), body:3f × boneCount (NaN = no body).
    /// Strings are a 7-bit-encoded length + UTF-8 (BinaryWriter).
    /// </summary>
    private static void Write(string path, string name, Stats stats, float facing, Vector3 activatePosition, float activateGroundY,
        List<Vector3> groundRays, List<Vector3> triangles, List<Frame> frames)
    {
        using var w = new BinaryWriter(File.Create(path), Encoding.UTF8);
        w.Write("DSTRAJ01"u8);
        w.Write(name);
        w.Write(stats.IsPlayer);
        w.Write(stats.Humanoid);
        w.Write(facing);
        V(w, activatePosition);
        w.Write(activateGroundY);
        w.Write(stats.Water);
        w.Write(Bones.Length);
        foreach (var b in Bones) w.Write(b);
        w.Write(groundRays.Count);
        foreach (var p in groundRays) V(w, p);
        w.Write(triangles.Count);
        foreach (var p in triangles) V(w, p);
        w.Write(frames.Count);
        foreach (var f in frames)
        {
            w.Write(f.Time);
            w.Write(f.Dt);
            w.Write(f.PhysicsStarted);
            w.Write(f.PoseSource);
            w.Write(f.PoseFresh);
            w.Write(f.GroundY);
            foreach (var p in f.Bone) V(w, p);
            foreach (var p in f.Body) V(w, p);
        }
    }

    private static void V(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }
}
