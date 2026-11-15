using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace CutsceneCamera;

/// <summary>
/// Offline checks of the plugin's cutscene shot code (`-- verify`), run against the installed game:
/// <list type="number">
/// <item>parser regression: the digest of the 149 curated entries (shots, every curve key, eye/aim/up/FOV at every
/// frame) has the hash recorded when the parser still lived in this tool;</item>
/// <item>the plugin's catalog and loader read every entry;</item>
/// <item>sampling at the start, middle and end of every entry gives a finite pose;</item>
/// <item>anchoring on a synthetic standing actor (moved and turned) is the curated frame moved and turned, and matches
/// this tool's own bind mapping; head-attached aims look at the head;</item>
/// <item>anchoring on a synthetic body lying on its back: root-height points land on the head / pelvis, the eye stays
/// above the floor, and head-aimed shots still look at the head;</item>
/// <item>role mapping for the five cases (and the skips);</item>
/// <item>view-matrix layout detection, projection FOV rewrite, blend.</item>
/// </list>
/// Exit code 1 when any check fails.
/// </summary>
internal static class Verify
{
    /// <summary>SHA-256 of `-- digest` written by the parser before it moved into the plugin (2026-11-15, game data of
    /// that day). A game update that touches these cutscenes changes it too; then regenerate both and compare the two
    /// parsers on the new data.</summary>
    private const string DigestSha256 = "e8e806bd9d4d247c926edf800ee442dc9c098b825d6b730fdb4e79cad48070fb";

    private static int failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) failures++;
    }

    public static int Run(Lumina.GameData gd, Func<string, CutbFile?> load, Bind.Body body, string repoRoot)
    {
        var jsonPath = Path.Combine(repoRoot, "CombatSimulator", "Resources", "CutsceneShots.json");

        Console.WriteLine("1. parser regression digest");
        var digest = Digest.Build(jsonPath, load, body);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digest))).ToLowerInvariant();
        Check(hash == DigestSha256, $"digest {digest.Count(c => c == '\n')} lines, sha256 {hash[..16]}… (recorded {DigestSha256[..16]}…)");

        Console.WriteLine("2. catalog and loader");
        var catalog = CutsceneShotCatalog.Parse(File.ReadAllText(jsonPath));
        Check(catalog.Entries.Count == 149, $"catalog entries {catalog.Entries.Count}");
        var loader = new CutsceneShotLoader(p => gd.GetFile(p)?.Data);
        var loaded = new List<LoadedCutsceneShot>();
        var loadErrors = new List<string>();
        foreach (var e in catalog.Entries)
        {
            var l = loader.Load(e, out var err);
            if (l != null) loaded.Add(l); else loadErrors.Add($"{e.Id}: {err}");
        }
        Check(loadErrors.Count == 0, $"loaded {loaded.Count}/{catalog.Entries.Count}" + (loadErrors.Count > 0 ? " — " + string.Join("; ", loadErrors.Take(3)) : ""));
        var durationOff = loaded.Count(l => MathF.Abs(l.Seconds - l.Entry.Duration) > 0.006f);
        Check(durationOff == 0, $"segment length equals the listed duration for {loaded.Count - durationOff}/{loaded.Count}");
        var races = new ushort[] { 101, 201, 401, 501, 601, 701, 801, 901, 1101, 1201, 1301, 1401, 1501, 1601, 1701, 1801 };
        var raceTables = races.Select(r => (r, t: loader.BindPoints(r))).ToList();
        Check(raceTables.All(x => x.t != null && x.t.ContainsKey(76) && x.t.ContainsKey(71)), "bind point tables of the human races read (fallback to c0101 where a race has none)");
        Console.WriteLine("     face height (pt76) per race: " + string.Join(" ", raceTables.Select(x => $"c{x.r:D4}={x.t![76].Offset.Y:0.00}")));
        var search = catalog.Entries.Count(e => e.Matches("orbit close"));
        Check(search > 0 && catalog.Entries.All(e => e.Matches("")), $"search \"orbit close\" finds {search}");

        // Synthetic actors on the reference skeleton.
        var refTable = loader.BindPoints(CutsceneShotLoader.ReferenceRace)!;
        CutsceneActor Standing(Vector3 pos, float yaw)
        {
            var m = CutsceneAnchoring.Facing(yaw) * Matrix4x4.CreateTranslation(pos);
            return new CutsceneActor { Label = "standing", Position = pos, Yaw = yaw, BindPoints = refTable, Bone = n => BoneOf(body, n, m), FloorY = pos.Y };
        }
        // On its back: the reference body turned -90° about its local X (head towards -Z... then turned by yaw),
        // pelvis 0.15 m above the ground.
        CutsceneActor Lying(Vector3 pos, float yaw)
        {
            var pelvis = body.Skel.Model[body.Skel.Index("j_kosi")].Translation;
            var m = Matrix4x4.CreateTranslation(-pelvis) * Matrix4x4.CreateRotationX(-MathF.PI / 2f)
                    * Matrix4x4.CreateTranslation(0, 0.15f, 0) * CutsceneAnchoring.Facing(yaw) * Matrix4x4.CreateTranslation(pos);
            return new CutsceneActor { Label = "lying", Position = pos, Yaw = yaw, Down = true, BindPoints = refTable, Bone = n => BoneOf(body, n, m), FloorY = pos.Y };
        }
        var other0 = Matrix4x4.CreateRotationY(MathF.PI) * Matrix4x4.CreateTranslation(0, 0, 2f);
        CutsceneActor Other(Matrix4x4 frameOfA)
        {
            var m = other0 * frameOfA;
            return new CutsceneActor { Label = "other", Position = m.Translation, Yaw = MathF.Atan2(m.M31, m.M33), BindPoints = refTable, Bone = n => BoneOf(body, n, m), FloorY = m.Translation.Y };
        }

        Console.WriteLine("3. sampling at start / middle / end");
        var nonFinite = 0;
        foreach (var l in loaded)
        {
            var a = Standing(Vector3.Zero, 0f);
            var b = Other(Matrix4x4.Identity);
            foreach (var t in new[] { 0f, l.Seconds / 2f, l.Seconds })
                if (!CutscenePlayback.Evaluate(l, t, Anchor(l.Entry, a, b)).Pose.IsFinite) nonFinite++;
        }
        Check(nonFinite == 0, $"finite poses {loaded.Count * 3 - nonFinite}/{loaded.Count * 3}");
        var cutsAt = loaded.Where(l => l.Spans.Count > 1).Take(20).ToList();
        var cutOk = 0;
        foreach (var l in cutsAt)
        {
            var a = Standing(Vector3.Zero, 0f);
            var b = Other(Matrix4x4.Identity);
            var boundary = (l.Spans[0].Span.To - l.Spans[0].Span.From) / CutscenePlayback.Fps;
            var before = CutscenePlayback.Evaluate(l, boundary - 0.01f, Anchor(l.Entry, a, b));
            var after = CutscenePlayback.Evaluate(l, boundary + 0.0001f, Anchor(l.Entry, a, b));
            if (before.SpanIndex == 0 && after.SpanIndex == 1 && after.ShotIndex == l.Spans[1].Shot.Index) cutOk++;
        }
        Check(cutOk == cutsAt.Count, $"cuts land on the span boundary {cutOk}/{cutsAt.Count}");

        Console.WriteLine("4. anchoring, standing (actor moved to (12.5, 3, -40) and turned 2.1 rad)");
        var pos = new Vector3(12.5f, 3f, -40f);
        const float yawT = 2.1f;
        var move = CutsceneAnchoring.Facing(yawT) * Matrix4x4.CreateTranslation(pos);
        float maxEye = 0, maxFrame = 0;
        var headAngles = new List<float>();
        foreach (var l in loaded)
        {
            var a0 = Standing(Vector3.Zero, 0f); var b0 = Other(Matrix4x4.Identity);
            var a1 = Standing(pos, yawT); var b1 = Other(move);
            foreach (var t in new[] { 0f, l.Seconds / 2f, l.Seconds })
            {
                var p0 = CutscenePlayback.Evaluate(l, t, Anchor(l.Entry, a0, b0)).Pose;
                var p1 = CutscenePlayback.Evaluate(l, t, Anchor(l.Entry, a1, b1)).Pose;
                maxEye = MathF.Max(maxEye, Vector3.Distance(Vector3.Transform(p0.Eye, move), p1.Eye));
                maxEye = MathF.Max(maxEye, Vector3.Distance(Vector3.Transform(p0.Target, move), p1.Target));
            }
            foreach (var pt in l.Entry.AttachPoints)
            {
                var plugin = CutsceneAnchoring.Frame(a1, pt, out _).Translation;
                var tool = Bind.PointFrame(Bind.Mapping.EidBindPoint, body, pt, move).Translation;
                maxFrame = MathF.Max(maxFrame, Vector3.Distance(plugin, tool));
            }
            if (HeadAimed(l) && !l.Entry.TwoPerson)
            {
                var mid = CutscenePlayback.Evaluate(l, l.Seconds / 2f, Anchor(l.Entry, a1, b1)).Pose;
                headAngles.Add(AngleTo(mid, a1.Bone("j_kao")!.Value.Position));
            }
        }
        Check(maxEye < 1e-3f, $"eye/target follow the actor's move and turn exactly (max error {maxEye * 1000:0.000} mm)");
        Check(maxFrame < 1e-3f, $"bind point frames equal the tool's mapping (max {maxFrame * 1000:0.000} mm)");
        headAngles.Sort();
        Check(headAngles.Count > 0 && headAngles[headAngles.Count / 2] < 5f,
            $"single shots aimed at a head point look at the head: {headAngles.Count} shots, median {Median(headAngles):0.0}°, max {headAngles.LastOrDefault():0.0}°");

        Console.WriteLine("5. anchoring, body lying on its back");
        var lyingActor = Lying(pos, yawT);
        var head = lyingActor.Bone("j_kao")!.Value.Position;
        var pelvisPos = lyingActor.Bone("j_kosi")!.Value.Position;
        var f76 = CutsceneAnchoring.Frame(lyingActor, 76, out var n76).Translation;
        var f42 = CutsceneAnchoring.Frame(lyingActor, 42, out _).Translation;
        var f71 = CutsceneAnchoring.Frame(lyingActor, 71, out var n71).Translation;
        Check(Vector3.Distance(f76, head) < 1e-4f && Vector3.Distance(f42, head) < 1e-4f, $"pt76 / pt42 → head ({n76})");
        Check(Vector3.Distance(f71, pelvisPos) < 1e-4f, $"pt71 → pelvis ({n71})");
        Check(head.Y < pos.Y + 0.4f, $"the synthetic body lies (head {head.Y - pos.Y:0.00} m above the floor)");
        var lyingHead = new List<float>();
        var lyingRoot = new List<float>();
        var below = 0;
        var clamped = 0; var clampedCorpse = 0; var corpseSamples = 0;
        var lyingSamples = 0;
        foreach (var l in loaded)
        {
            var a = Lying(pos, yawT);
            var b = Other(move);
            foreach (var t in new[] { 0f, l.Seconds / 2f, l.Seconds })
            {
                var raw = CutscenePlayback.Evaluate(l, t, Anchor(l.Entry, a, b)).Pose;
                if (raw.Eye.Y < a.FloorY + 0.15f) { clamped++; if (l.Entry.CorpseSuitable) clampedCorpse++; }
                if (l.Entry.CorpseSuitable) corpseSamples++;
                var p = CutscenePlayback.ClampEye(raw, a.FloorY, 0.15f);
                lyingSamples++;
                if (p.Eye.Y < a.FloorY + 0.149f || !p.IsFinite) below++;
            }
            if (l.Entry.TwoPerson) continue;
            var mid = CutscenePlayback.Evaluate(l, l.Seconds / 2f, Anchor(l.Entry, a, b)).Pose;
            if (HeadAimed(l)) lyingHead.Add(AngleTo(mid, head));
            else if (AimedAt(l, 76)) lyingRoot.Add(AngleTo(mid, head));
        }
        Check(below == 0, $"eye above the floor after the clamp {lyingSamples - below}/{lyingSamples} (the clamp lifted {clamped}; of the corpse-suitable entries {clampedCorpse}/{corpseSamples})");
        Check(lyingHead.Count > 0 && Median(lyingHead) < 8f, $"head-point aims on the lying body look at the head: {lyingHead.Count} shots, median {Median(lyingHead):0.0}°");
        Check(lyingRoot.Count > 0 && Median(lyingRoot) < 15f, $"pt76 aims remapped to the lying head look at it: {lyingRoot.Count} shots, median {Median(lyingRoot):0.0}°");

        Console.WriteLine("6. roles");
        nint P = 1, K = 2, E = 3, A = 4, N = 5, T = 6;
        CutsceneRoleResult? R(bool two, CutsceneTrigger tr, bool alive, nint finalAlly = 4, nint killer = 2, nint lastEnemy = 3, nint nearest = 5)
            => CutsceneRoles.Resolve(two, new CutsceneRoleInput(tr, alive, P, killer, lastEnemy, finalAlly, nearest, T), out _);
        Check(R(false, CutsceneTrigger.Death, false) is { Subject: var s1, Other: 0 } && s1 == P, "death, single: subject = player");
        Check(R(true, CutsceneTrigger.Death, false) is { } r2 && r2.Subject == P && r2.Other == K, "death, two-person: subject = player, other = killer");
        Check(R(false, CutsceneTrigger.Victory, true) is { } r3 && r3.Subject == P
              && R(false, CutsceneTrigger.Victory, false) is { } r3b && r3b.Subject == A, "victory, single: player if alive, else the deciding ally");
        Check(R(true, CutsceneTrigger.Victory, true) is { } r4 && r4.Subject == P && r4.Other == E, "victory, two-person, player alive: subject = player, other = last enemy to fall");
        Check(R(true, CutsceneTrigger.Victory, false) is { } r5 && r5.Subject == A && r5.Other == E
              && R(true, CutsceneTrigger.Victory, false, finalAlly: 0) is { } r5b && r5b.Subject == N, "victory, two-person, player dead: subject = final-blow ally (else nearest living ally), other = last enemy");
        Check(R(true, CutsceneTrigger.Death, false, killer: 0) == null && R(true, CutsceneTrigger.Victory, false, finalAlly: 0, nearest: 0) == null
              && R(true, CutsceneTrigger.Victory, true, lastEnemy: 0) == null, "a missing required actor skips the shot");
        Check(R(true, CutsceneTrigger.PlayNow, true) is { } r6 && r6.Subject == P && r6.Other == T, "play now: subject = player, other = target");
        var followOther = catalog.Entries.First(e => e.TwoPerson && e.Follow == "other");
        var followSubject = catalog.Entries.First(e => e.TwoPerson && e.Follow == "subject");
        var roles = new CutsceneRoleResult(P, K, "");
        Check(CutsceneRoles.ActorFor(followOther, followOther.FollowActor, roles) == K && CutsceneRoles.ActorFor(followOther, followOther.LookActor!.Value, roles) == P
              && CutsceneRoles.ActorFor(followSubject, followSubject.FollowActor, roles) == P && CutsceneRoles.ActorFor(followSubject, followSubject.LookActor!.Value, roles) == K,
            "two-person: the followed cutscene actor takes the entry's follow role, the looked-at one its lookAt role");

        Console.WriteLine("7. lens, pose hand-over, blend");
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.8f, 16f / 9f, 0.1f, 1000f);
        Check(MathF.Abs(CutsceneView.VerticalFovOf(proj) - 0.8f) < 1e-5f, "vertical FOV read off a projection matrix");
        // The game's FoV field renders some lens; the measured ratio maps a wanted lens back to the field value.
        var ratio = CutsceneView.FovRatio(0.9f, 0.78f);
        var field = ratio is { } r0 ? CutsceneView.GameFovFor(0.5f, r0) : null;
        Check(ratio is { } r1 && field is { } f1 && MathF.Abs(2f * MathF.Atan(MathF.Tan(f1 / 2f) * r1) - 0.5f) < 1e-5f
              && CutsceneView.FovRatio(float.NaN, 0.78f) == null && CutsceneView.FovRatio(0.9f, 0f) == null,
            $"FoV field for a wanted lens round-trips through the measured ratio ({field ?? float.NaN:0.0000})");
        Check(CutsceneView.GameFovFor(3.1f, 1f) is { } wide && MathF.Abs(wide - CutsceneView.MaxFov) < 1e-4f
              && CutsceneView.GameFovFor(1e-4f, 1f) is { } narrow && MathF.Abs(narrow - CutsceneView.MinFov) < 1e-5f
              && CutsceneView.GameFovFor(float.NaN, 1f) == null && CutsceneView.GameFovFor(0.5f, float.NaN) == null,
            "asked-for lens clamped to [1°, 170°]; non-finite input gives no FoV request");
        Check(CutsceneView.CanHandOver(new Vector3(3, 2, 1), new Vector3(-1, 1.5f, 4))
              && !CutsceneView.CanHandOver(Vector3.Zero, new Vector3(0.05f, 0, 0))
              && !CutsceneView.CanHandOver(new Vector3(float.NaN, 0, 0), Vector3.UnitZ)
              && !CutsceneView.CanHandOver(Vector3.Zero, new Vector3(0, float.PositiveInfinity, 0)),
            "only finite eye / look-at pairs at least 0.1 m apart are handed to the game");
        var pa = new CutPose(Vector3.Zero, Vector3.UnitZ, 0.5f);
        var pb = new CutPose(Vector3.One, Vector3.One + Vector3.UnitX, 1f);
        Check(CutPose.Lerp(pa, pb, 0) == pa && CutPose.Lerp(pa, pb, 1).Eye == pb.Eye && CutPose.Ease(0.5f) == 0.5f, "blend endpoints");

        Console.WriteLine("8. bad camera rigs");
        // (node, code, parent): the eye chain (code 128) ends on a node whose ancestors are a rig level (code 64).
        CameraShot Rig(params (byte Node, byte Code, sbyte Parent)[] nodes)
        {
            var s = new CameraShot { Channels = new List<CurveChannel>() };
            foreach (var (node, code, parent) in nodes)
                s.Channels.Add(new CurveChannel { Group = 8, Code = code, Node = node, Parent = parent });
            return s;
        }
        var selfLoop = Rig((3, 128, 2), (2, 64, 2));
        var cycle = Rig((3, 128, 2), (2, 64, 1), (1, 64, 2));
        var deep = Rig(Enumerable.Range(0, 20).Select(i => ((byte)i, (byte)(i == 19 ? 128 : 64), (sbyte)(i - 1))).ToArray());
        var fine = Rig((1, 64, -1), (2, 64, 1), (3, 128, 2));
        Check(!selfLoop.RigIsAcyclic(out _) && !cycle.RigIsAcyclic(out _) && !deep.RigIsAcyclic(out _) && fine.RigIsAcyclic(out _),
            "self / cyclic / over-deep parent chains are rejected, a normal chain is not");
        bool Throws(CameraShot s) { try { s.Evaluate(0f, _ => Matrix4x4.Identity); return false; } catch (InvalidDataException) { return true; } }
        Check(Throws(selfLoop) && Throws(cycle) && Throws(deep) && !Throws(fine), "evaluating a cyclic or over-deep rig fails with an error instead of recursing forever");

        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static Func<Attach, Matrix4x4> Anchor(CutsceneShotEntry e, CutsceneActor a, CutsceneActor b) => at =>
    {
        if (at.Actor == 255) return Matrix4x4.Identity;
        var actor = at.Actor == e.FollowActor ? a : b;
        return CutsceneAnchoring.Frame(actor, at.Point, out _);
    };

    private static (Vector3, Quaternion?)? BoneOf(Bind.Body body, string name, Matrix4x4 actor)
    {
        var i = body.Skel.Index(name);
        if (i < 0) return null;
        var m = body.Skel.Model[i] * actor;
        return (m.Translation, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m)));
    }

    private static readonly int[] HeadPoints = { 1, 25, 26, 27, 43, 44, 159 };

    /// <summary>Every shot of the segment aims at a head point of the followed actor itself.</summary>
    private static bool HeadAimed(LoadedCutsceneShot l) =>
        l.Spans.All(s => s.Shot.Aim.Actor == l.Entry.FollowActor && HeadPoints.Contains(s.Shot.Aim.Point));

    private static bool AimedAt(LoadedCutsceneShot l, int point) =>
        l.Spans.All(s => s.Shot.Aim.Actor == l.Entry.FollowActor && s.Shot.Aim.Point == point);

    private static float AngleTo(CutPose p, Vector3 point)
    {
        var to = point - p.Eye;
        if (to.LengthSquared() < 1e-8f) return 0f;
        return MathF.Acos(Math.Clamp(Vector3.Dot(p.Forward, Vector3.Normalize(to)), -1f, 1f)) * 180f / MathF.PI;
    }

    private static float Median(List<float> v)
    {
        if (v.Count == 0) return float.NaN;
        var s = v.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }
}
