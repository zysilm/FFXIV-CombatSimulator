// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Globalization;
using CombatSimulator.Animation;
using CombatSimulator.Animation.Hosting;
using CombatSimulator.Animation.Recording;
using RagdollReplay;

// Usage:
//   RagdollReplay <file.rgdrec | folder> [--keep] [--synthetic-meshes | --synthetic-colliders | --synthetic-hair]
//   RagdollReplay <file.rgdrec | folder> --extensions   (stage A3b: the extension points, see ExtensionCheck.cs)
// Replays each recording through the plugin's RagdollController and reports, per file, "identical" or the
// first difference. Exit code 0 when every file replayed identically.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: RagdollReplay <file.rgdrec | folder> [--keep] [--synthetic-meshes | --synthetic-colliders | --synthetic-hair]");
    return 2;
}
if (args.Contains("--extensions")) return ExtensionCheck.Run(args[0]);
var keep = args.Contains("--keep");
var synthetic = args.Contains("--synthetic-meshes") || args.Contains("--synthetic-colliders") || args.Contains("--synthetic-hair");
SyntheticSettings.MeshVolumes = args.Contains("--synthetic-meshes");
SyntheticSettings.Hair = args.Contains("--synthetic-hair");
var target = args[0];
var files = Directory.Exists(target)
    ? Directory.GetFiles(target, "*" + RagdollRecordingFormat.FileExtension).OrderBy(p => p, StringComparer.Ordinal).ToArray()
    : new[] { target };

Console.WriteLine($"runtime {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
                  $"vector width {System.Numerics.Vector<float>.Count}, BepuPhysics {RagdollRecordingBepu.BepuVersion()}");
var identical = 0;
var results = new List<(string File, string Outcome)>();
foreach (var file in files)
{
    var clock = Stopwatch.StartNew();
    string outcome;
    try
    {
        if (!synthetic)
            outcome = ReplayDriver.Run(file, keep, false, out _);
        else
        {
            // Two runs: the first with made-up models (SyntheticMeshes.cs), the second replays its recording.
            var first = ReplayDriver.Run(file, true, true, out var produced);
            outcome = produced == null
                ? "SYNTHETIC RUN FAILED: " + first
                : ReplayDriver.Run(produced, keep, false, out _) + "\n    synthetic mesh products: " + ReplayDriver.MeshProducts(produced);
        }
    }
    catch (ReplayUnsupportedException ex)
    {
        outcome = "NOT REPLAYED: " + ex.Message;
    }
    catch (Exception ex)
    {
        outcome = "REPLAY ERROR: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace;
    }
    if (outcome.StartsWith("IDENTICAL (", StringComparison.Ordinal)) identical++;
    results.Add((Path.GetFileName(file), outcome));
    Console.WriteLine($"{Path.GetFileName(file)} ({clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s): {outcome}");
}
Console.WriteLine($"== {identical} of {files.Length} recording(s) replayed bit-identically.");
return identical == files.Length ? 0 : 1;

internal static class ReplayDriver
{
    public static string Run(string path, bool keep, bool synthetic, out string? produced, ReplayHooks? hooks = null)
    {
        var source = new ReplaySource(path);
        var header = source.Header;
        if (header[2].AsBool)
            throw new ReplayUnsupportedException("recorded with the private module (DEV_EXPERIMENTAL); replay it with a build that has it");
        if (!header[3].AsBool)
            throw new ReplayUnsupportedException("not recorded with a PUBLIC_BUILD plugin");
        if (RagdollRecordingAnalysis.Summarize(path).CarriedState == null)
            throw new ReplayUnsupportedException("the Activate record holds no carried-over state");
        // A version 2 recording has the mesh products only where the controller used them, which is not enough to
        // answer the mesh paths (stage A2b records what the mesh source hands over: version 3).
        if (!source.HasMeshProducts)
        {
            foreach (var index in source.IndicesOf(RecordType.Decision))
                if (source.Records[index][0].AsString == "MeshFrameEarlyOut")
                    throw new ReplayUnsupportedException("mesh volumes were on (mesh-frame early out): a version 2 recording cannot answer the mesh source");
            if (source.IndicesOf(RecordType.MeshLod).Count > 0 || source.IndicesOf(RecordType.Triangles).Count > 0 ||
                source.IndicesOf(RecordType.HairMesh).Count > 0)
                throw new ReplayUnsupportedException("uses mesh products (reduced meshes, mesh colliders or hair meshes): a version 2 recording cannot answer the mesh source");
        }

        var skeletons = new ReplaySkeletons(source);
        var log = new ReplayLog();
        var clock = new ReplayClock(source);
        var meshes = new ReplayMeshSource(source);
        var actors = new ReplayActors(source, skeletons);
        var host = new RagdollHost
        {
            Skeletons = skeletons,
            World = new ReplayWorld(source),
            Actors = actors,
            Colliders = new ReplayColliders(source),
            Settings = synthetic ? new SyntheticSettings(new ReplaySettings(source)) : new ReplaySettings(source),
            Log = log,
            Clock = clock,
            Random = new ReplayRandom(source),
            Meshes = synthetic ? new SyntheticMeshSource(meshes, skeletons) : meshes,
        };

        // The target as the game showed it when the ragdoll was armed: its first recorded pose.
        var actor = (nint)source.Activate[0].AsULong;
        var (firstPose, firstPoseIndex) = source.Peek(RecordType.SkeletonPose, r => (nint)r[0].AsULong == actor);
        if (firstPose != null) skeletons.Load(firstPose, firstPoseIndex);

        var controller = new RagdollController(host);
        var a = source.Activate;
        controller.RunAnimatedHair = a[11].AsBool;
        controller.GrabRigid = a[12].AsBool;
        if (a[13].AsBool)
            controller.CarryExternalRigOnAnchor = _ =>
            {
                var r = source.Take(RecordType.Decision, x => x[0].AsString == "CarryExternalRigOnAnchor");
                if (r == null) { source.Diverged("carry decision asked, none recorded"); return false; }
                return r[1].AsBool;
            };
        var carried = CarriedState(source, meshes);
        carried.LiveHairRunning = LiveHairRunning(source);
        carried.LiveHairDraw = skeletons.Known(actor)?.DrawIdentity ?? actor;
        carried.LiveHairObject = actors.GetGameObjectId(actor);
        if (carried.HasBodySurfaces)
        {
            // The target reads as its Activate entity; the others as the carried surfaces name them.
            if (carried.FrozenMeshActor != 0 && carried.FrozenMeshActor != actor) actors.KnownEntities[carried.FrozenMeshActor] = carried.FrozenMeshEntity;
            foreach (var entry in carried.WeaponContactMeshes)
                if (entry.Actor != actor) actors.KnownEntities[entry.Actor] = entry.Entity;
        }
        controller.RestoreCarriedState(carried);

        var folder = Path.Combine(Path.GetTempPath(), "AdvancedRagdollReplay", "out", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        RagdollRecordingSession.SecondsLimit = double.PositiveInfinity;
        RagdollRecordingSession.Configure(folder, log);
        // The synthetic hair check starts the way the player's controller does in the game: the live hair rig is
        // already running when the player dies (one render frame of the inactive controller builds it). The
        // recording then starts with that rig carried over, and the second run has to see that from the recording.
        if (synthetic && SyntheticSettings.Hair && controller.RunAnimatedHair) controller.ReplayRenderFrame();
        RagdollRecordingSession.Start();
        string? replayed = null;
        try
        {
            hooks?.BeforeActivate?.Invoke(controller);
            controller.Activate(actor, a[1].AsFloat, (RagdollTriggerMode)a[2].AsInt, a[3].AsFloat);
            var frames = source.IndicesOf(RecordType.RenderFrame);
            var nextFrame = 0;
            for (var i = source.ActivateIndex + 1; i < source.Records.Count; i++)
            {
                var record = source.Records[i];
                if (record.Type == RecordType.RenderFrame)
                {
                    while (nextFrame < frames.Count && frames[nextFrame] <= i) nextFrame++;
                    source.FrameIndex = i;
                    source.FrameEnd = nextFrame < frames.Count ? frames[nextFrame] : source.Records.Count;
                    source.RenderFrame = record[0].AsInt;
                    clock.AdvanceFrame(record[1].AsFloat);
                    hooks?.BeforeFrame?.Invoke(controller);
                    controller.ReplayRenderFrame();
                }
                else if (record.Type == RecordType.ApiCall && record[0].AsInt == 0)
                {
                    Call(controller, record, source);
                }
                else if (record.Type == RecordType.End)
                {
                    if (record[0].AsString != "deactivated") RagdollRecordingSession.Stop();
                    break;
                }
            }
        }
        finally
        {
            RagdollRecordingSession.Stop();
            controller.Dispose();
            replayed = Directory.GetFiles(folder, "*" + RagdollRecordingFormat.FileExtension).SingleOrDefault();
        }
        produced = null;
        if (hooks != null) hooks.Replayed = replayed;
        if (replayed == null) return "REPLAY ERROR: the replay wrote no recording";
        if (synthetic)
        {
            produced = replayed;
            return "synthetic run: " + replayed;
        }

        var difference = source.HasMeshProducts
            ? RagdollRecordingAnalysis.Compare(path, replayed,
                record => record.Type is not (RecordType.Header or RecordType.End or RecordType.Note or RecordType.MeshCache),
                EqualInputs)
            :RagdollRecordingAnalysis.Compare(path, replayed, IsComparableWithVersion2, EqualToVersion2);
        var leftovers = Leftovers(source);
        var text = (difference.Identical ? "IDENTICAL (" : "DIFFERENT: ") + difference.Description + (difference.Identical ? ")" : string.Empty);
        if (difference.InputDifference != null) text += "\n    first input difference: " + difference.InputDifference;
        if (source.Divergence != null) text += $"\n    replay host: {source.DivergenceCount} unanswered or different question(s); first: {source.Divergence}";
        if (leftovers.Length > 0) text += "\n    recorded but never asked: " + leftovers;
        foreach (var problem in log.Problems.Take(3)) text += "\n    controller " + problem;
        if (!difference.Identical || difference.InputDifference != null || source.Divergence != null) text = text.Replace("IDENTICAL (", "IDENTICAL OUTPUTS, INPUTS NOT (");
        if (!keep) Directory.Delete(folder, true);
        else text += "\n    replayed recording: " + replayed;
        return text;
    }

    /// <summary>How many mesh products of each kind a recording holds (the synthetic self-check reports what it exercised).</summary>
    public static string MeshProducts(string path)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        using var reader = RagdollRecordingReader.Open(path);
        foreach (var record in reader.ReadAll())
        {
            if (record.Type != RecordType.MeshProduct) continue;
            var kind = record[0].AsString ?? "?";
            if (kind == MeshProductKind.Lod) kind += record[3].AsBool ? " (ready)" : " (not ready)";
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }
        return counts.Count == 0 ? "none" : string.Join(", ", counts.Select(c => $"{c.Value} {c.Key}"));
    }

    // A version 2 original against its version 3 replay: what version 3 added (the mesh products, the clock readings
    // other than the walking-support query stamp) is left out, and the Activate record is compared on what version 2
    // wrote (version 3 appends followWasActive).
    private static bool IsComparableWithVersion2(RagdollRecord record) => record.Type switch
    {
        RecordType.Header or RecordType.End or RecordType.Note or RecordType.MeshCache or RecordType.MeshProduct => false,
        RecordType.Clock => record[0].AsString == nameof(ClockSite.NpcTraversalQuery),
        _ => true,
    };

    private static bool EqualToVersion2(RagdollRecord original, RagdollRecord replayed)
    {
        if (original.Type != RecordType.Activate) return original.ContentEquals(replayed);
        if (replayed.Type != RecordType.Activate || replayed.Values.Length != original.Values.Length + 1) return false;
        for (var i = 0; i < original.Values.Length; i++)
            if (!original.Values[i].Equals(replayed.Values[i])) return false;
        return true;
    }

    // Version 3: every value, except the label of another character's triangle soup. The label is the character's
    // name as the collider provider listed it; the controller only puts it into log lines, and the provider's names
    // are not recorded (ActorList holds addresses), so the replay cannot hand the same one back. It stays in the
    // record to show whose mesh it is.
    private static bool EqualInputs(RagdollRecord original, RagdollRecord replayed)
    {
        if (original.Type != RecordType.MeshProduct || original[0].AsString != MeshProductKind.ActorTriangles)
            return original.ContentEquals(replayed);
        if (replayed.Type != RecordType.MeshProduct || replayed[0].AsString != MeshProductKind.ActorTriangles ||
            replayed.Values.Length != original.Values.Length) return false;
        for (var i = 0; i < original.Values.Length; i++)
            if (i != ActorTrianglesLabel && !original.Values[i].Equals(replayed.Values[i])) return false;
        return true;
    }

    private const int ActorTrianglesLabel = 2;

    /// <summary>
    /// Whether the local player's live hair rig was running, and current, when the recording started. It is state
    /// from before the activation (the player's controller runs it the whole time the player is alive) that the
    /// Activate record does not list, but the recording shows it: every build of a hair rig writes a HairMesh
    /// record, and the live hair reads the clock ("AnimatedHair") after it has decided whether to rebuild and before
    /// it builds. So the frame of the first such reading has a HairMesh record exactly when the game did not find a
    /// current rig; without one, the rig was running and current.
    /// </summary>
    private static bool LiveHairRunning(ReplaySource source)
    {
        if (!source.HasMeshProducts || !source.Activate[11].AsBool) return false;
        var (clock, at) = source.Peek(RecordType.Clock, r => r[0].AsString == nameof(ClockSite.AnimatedHair));
        if (clock == null) return false;
        for (var i = at + 1; i < source.Records.Count; i++)
        {
            var type = source.Records[i].Type;
            if (type is RecordType.RenderFrame or RecordType.End) return true;
            if (type == RecordType.HairMesh) return false;
        }
        return true;
    }

    private static string Leftovers(ReplaySource source)
    {
        var parts = new List<string>();
        void Check(RecordType type, string name, Func<RagdollRecord, bool>? match = null)
        {
            var n = source.Unconsumed(type, match);
            if (n > 0) parts.Add($"{n} {name}");
        }
        Check(RecordType.Raycast, "raycast(s)");
        Check(RecordType.GroundTriangles, "ground triangle query(ies)");
        Check(RecordType.WaterSurface, "water query(ies)");
        Check(RecordType.ActorPosition, "actor position(s)", r => r[0].AsInt != (int)ActorSite.TraversalPreviewRoot);
        Check(RecordType.SkeletonPose, "skeleton pose(s)");
        Check(RecordType.Random, "random draw(s)");
        Check(RecordType.Clock, "clock stamp(s)");
        Check(RecordType.BoneWorldPosition, "bone position(s)");
        Check(RecordType.WeaponSample, "weapon sample(s)");
        Check(RecordType.TriggerInputs, "trigger input(s)");
        Check(RecordType.MeshProduct, "mesh product(s)", r => r[0].AsString != MeshProductKind.Carried);
        Check(RecordType.HairMesh, "hair sample set(s)");
        return string.Join(", ", parts);
    }

    private static RagdollController.ReplayCarriedState CarriedState(ReplaySource source, ReplayMeshSource meshes)
    {
        var a = source.Activate;
        var s = new RagdollController.ReplayCarriedState
        {
            PendingDirectedCollapse = a[14].AsBool,
            PendingProfile = a[15].AsString ?? string.Empty,
            PendingDuration = a[16].AsFloat,
            PendingFootForce = a[17].AsFloat,
            PendingPelvisForce = a[18].AsFloat,
            PendingDrop = a[19].AsFloat,
            PendingForward = a[20].AsFloat,
            PendingPitchDegrees = a[21].AsFloat,
            PendingEntryConditionedKneePowerLoss = a[22].AsBool,
            PendingKneePowerLoss = a[23].AsBool,
            UseLocalSeveredBones = a[24].AsBool,
        };
        var at = 25;
        var severed = a[at++].Count;
        for (var i = 0; i < severed; i++) s.LocalSeveredBones.Add(a[at++].AsString!);
        var lightweight = a[at++].Count;
        for (var i = 0; i < lightweight; i++) s.LightweightColliders.Add((nint)a[at++].AsULong);
        if (at != RagdollRecordingFormat.ActivateCarriedStateIndex(a))
            throw new ReplayUnsupportedException("the Activate record has an unexpected layout");
        s.ImpactRandomState = a[at++].AsUInt;
        s.NextGrabSlotId = a[at++].AsInt;
        s.ExternalBodyGeneration = a[at++].AsInt;
        var struck = a[at++].Count;
        for (var i = 0; i < struck; i++) s.StruckThisWindow.Add(a[at++].AsInt);
        var strikers = a[at++].Count;
        for (var i = 0; i < strikers; i++) s.StrikeColliders.Add((nint)a[at++].AsULong);
        var requests = a[at++].AsInt;
        for (var i = 0; i < requests; i++)
        {
            s.TraversalRequests.Add(new RagdollController.ReplayTraversalRequest(
                (nint)a[at].AsULong, a[at + 1].AsBool, a[at + 2].AsVector3, a[at + 3].AsVector3, a[at + 4].AsFloat,
                a[at + 5].AsFloat, a[at + 6].AsFloat, a[at + 7].AsFloat, a[at + 8].AsFloat, a[at + 9].AsInt, a[at + 10].AsBool));
            at += 11;
        }
        // Version 3 lists it at the end of the Activate record. Version 2 did not: public builds never reset it
        // between activations, and the first physics frame records the value it started with.
        s.FollowWasActive = source.HasMeshProducts
            ? a[at].AsBool
            : source.Peek(RecordType.FrameBegin).Record?[6].AsBool ?? false;
        meshes.ReadCarried(s);
        return s;
    }

    private static void Call(RagdollController controller, RagdollRecord call, ReplaySource source)
    {
        var method = call[1].AsString;
        switch (method)
        {
            case nameof(RagdollController.AddLiveCollider):
                controller.AddLiveCollider((nint)call[2].AsULong);
                break;
            case nameof(RagdollController.Deactivate):
                controller.Deactivate();
                break;
            case nameof(RagdollController.CreateGrabConstraint):
                controller.GrabRigid = call[8].AsBool;
                controller.CreateGrabConstraint(call[2].AsString!, call[3].AsVector3, (nint)call[4].AsULong,
                    call[5].AsFloat, call[6].AsFloat, call[7].AsFloat);
                break;
            case nameof(RagdollController.SetGrabTuning):
                controller.SetGrabTuning(call[2].AsInt, call[3].AsFloat, call[4].AsFloat, call[5].AsFloat);
                break;
            case nameof(RagdollController.UpdateGrabTarget):
                controller.UpdateGrabTarget(call[2].AsInt, call[3].AsVector3);
                break;
            case nameof(RagdollController.RemoveGrabConstraint):
                controller.RemoveGrabConstraint(call[2].AsInt);
                break;
            case nameof(RagdollController.GetWeaponContactMesh):
                controller.GetWeaponContactMesh((nint)call[2].AsULong);
                break;
            default:
                throw new ReplayUnsupportedException($"the recording calls {method}, which this replay does not drive yet");
        }
    }
}
