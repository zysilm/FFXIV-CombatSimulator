using System.Globalization;
using System.Numerics;
using CombatSimulator.Camera.DeathShots;
using CombatSimulator.Camera.Duel;
using DuelCamera;

// Offline check of the duel camera (docs/DUEL_CAMERA.md).
//   dotnet run -c Release                 unit checks, then the metric run over the scripted duels and its gates
//                                         (exit code 0 = all pass)
//   dotnet run -c Release -- unit         unit checks only
//   dotnet run -c Release -- events       the metric run, with what happened in each duel (opponent picks, sides, crossings)
//   dotnet run -c Release -- probe <part of a duel's name> [tight|standard|wide]   one run, four lines a second
// There are no recordings of two fighters, so every duel here is scripted: the numbers say what the solver does on
// these motions, not how the camera looks in the game.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var command = args.Length > 0 ? args[0] : "run";

var failures = 0;
if (command is "unit" or "run" or "events") failures += UnitChecks.Run();
if (command == "unit") return failures == 0 ? 0 : 1;

var scenarios = Scenarios.All();
if (command == "probe")
{
    var name = args.Length > 1 ? args[1] : "";
    var framing = args.Length > 2 && Enum.TryParse<DuelFraming>(args[2], true, out var fr) ? fr : DuelFraming.Standard;
    var s = scenarios.FirstOrDefault(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) ?? scenarios[0];
    var frames = Runner.Run(s, framing, Camera.Duel, out _);
    Console.WriteLine($"== {s.Name} ({framing})");
    foreach (var f in frames.Where((_, i) => i % (s.Fps / 4) == 0))
    {
        var o = f.Out;
        var fwd = Vector3.Normalize(o.LookAt - o.Eye);
        var p = DuelView.Project(o.Eye, fwd, o.Fov, Runner.Aspect, f.Player.At(0.7f));
        var e = f.Opponent is { } opp ? DuelView.Project(o.Eye, fwd, o.Fov, Runner.Aspect, opp.At(0.7f)) : default;
        Console.WriteLine($"{f.T,5:0.00} s  sep {o.Separation,5:0.0} m  off {o.OffAxis / DeathShotMath.Deg,5:0}°  side {o.Side,2}  dist {o.Distance:0.00} m  lens {o.Fov / DeathShotMath.Deg,4:0.0}°  " +
                          $"player x {p.X,5:0.00} y {p.Y,5:0.00}  opponent x {e.X,5:0.00} y {e.Y,5:0.00}  beat {o.Beat:0.00}  pull {o.Pull:0.00}  lift {o.Lift:0.00}  yaw {o.Yaw / DeathShotMath.Deg,5:0}°");
    }
    return 0;
}

Console.WriteLine();
Console.WriteLine("== metric run: scripted duels, duel camera (Standard), 16:9");
var results = new List<(Scenario S, Result R)>();
foreach (var s in scenarios)
{
    var frames = Runner.Run(s, DuelFraming.Standard, Camera.Duel, out var world);
    results.Add((s, Metrics.Measure(s, frames, world)));
}

static string Pct(int n, int of) => of > 0 ? (100f * n / of).ToString("0.0") : "–";
static string P(List<float> v, float p, string format = "0.00") => v.Count > 0 ? Metrics.Percentile(v, p).ToString(format) : "–";
static string Max(List<float> v, string format = "0") => v.Count > 0 ? v.Max().ToString(format) : "–";

Console.WriteLine("| duel | both in % | heads in % | player in % | opposite sides % | separation p5/p50/p95 | player size p5/p95 | opponent size p5/p95 |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");
foreach (var (s, r) in results)
    Console.WriteLine($"| {s.Name} | {Pct(r.BothIn, r.TwoFrames)} | {Pct(r.HeadsIn, r.TwoFrames)} | {Pct(r.PlayerIn, r.Frames)} | {Pct(r.OppositeSides, r.Separation.Count)} | " +
                      $"{P(r.Separation, 0.05f)}/{P(r.Separation, 0.5f)}/{P(r.Separation, 0.95f)} | {P(r.PlayerSize, 0.05f)}/{P(r.PlayerSize, 0.95f)} | {P(r.OpponentSize, 0.05f)}/{P(r.OpponentSize, 0.95f)} |");
Console.WriteLine();
Console.WriteLine("| duel | line crossings (asked / picked again) | view °/s p99/max | view °/s² p99 | eye about the player m/s p99/max | eye jerk m/s³ p99 | lens rad/s max | movement yaw: step °/frame p99/max | movement yaw off the rendered yaw ° p50/max | back after a dash s | both heads in after a change of opponent s | eye above ground min m | eye pulled in % | opponent hidden % |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
foreach (var (s, r) in results)
    Console.WriteLine($"| {s.Name} | {r.LineCrossings} ({r.SideChanges} / {r.Reanchors}) | {P(r.ViewRate, 0.99f, "0")}/{Max(r.ViewRate)} | {P(r.ViewAccel, 0.99f, "0")} | " +
                      $"{P(r.EyeSpeedRelative, 0.99f)}/{Max(r.EyeSpeedRelative, "0.00")} | {P(r.EyeJerk, 0.99f, "0")} | {Max(r.FovRate, "0.00")} | {P(r.MoveYawError, 0.99f)}/{Max(r.MoveYawError, "0.00")} | {P(r.ViewVsMove, 0.5f, "0")}/{Max(r.ViewVsMove)} | " +
                      $"{(r.MaxRecovery < 0f ? "–" : r.MaxRecovery.ToString("0.00"))} | {(r.MaxSwitch < 0f ? "–" : r.MaxSwitch.ToString("0.00"))} | {r.MinGround:0.00} | {Pct(r.PulledFrames, r.Frames)} | {Pct(r.OpponentHidden, r.TwoFrames)} |");

if (command == "events")
{
    Console.WriteLine();
    foreach (var (s, r) in results)
    {
        Console.WriteLine($"-- {s.Name}");
        foreach (var e in r.Events) Console.WriteLine("   " + e);
    }
}

// ---- key moments: timing of each push-in ----
Console.WriteLine();
Console.WriteLine("== key moments (duel \"key moments\"): push-in per scripted moment");
{
    var s = scenarios.First(x => x.Name == "key moments");
    var frames = Runner.Run(s, DuelFraming.Standard, Camera.Duel, out _);
    Console.WriteLine("| at s | moment | taken | peak | to 90 % of the peak s | over (< 0.05) after s | eye-to-player at the peak (× before) | lens at the peak (× before) | heads in % |");
    Console.WriteLine("|---:|---|---|---:|---:|---:|---:|---:|---:|");
    for (var b = 0; b < Scenarios.BeatTimes.Length; b++)
    {
        var (at, beat) = Scenarios.BeatTimes[b];
        var until = b + 1 < Scenarios.BeatTimes.Length ? Scenarios.BeatTimes[b + 1].At : s.Duration;
        var window = frames.Where(f => f.T >= at - 0.01f && f.T < until).ToList();
        var before = frames.Last(f => f.T < at - 0.01f);
        var startLevel = before.Out.Beat;
        var peak = window.Max(f => f.Out.Beat);
        var peakFrame = window.First(f => f.Out.Beat >= peak);
        var taken = peak > startLevel + 0.05f;
        var rise = window.FirstOrDefault(f => f.Out.Beat >= 0.9f * peak);
        var over = window.FirstOrDefault(f => f.T > peakFrame.T && f.Out.Beat < 0.05f);
        float Dist(Frame f) => Vector3.Distance(f.Out.Eye, f.Player.At(0.74f));
        var heads = window.Count(f => Metrics.Inside(DuelView.Project(f.Out.Eye, Vector3.Normalize(f.Out.LookAt - f.Out.Eye), f.Out.Fov, Runner.Aspect, f.Player.Head)) &&
                                      Metrics.Inside(DuelView.Project(f.Out.Eye, Vector3.Normalize(f.Out.LookAt - f.Out.Eye), f.Out.Fov, Runner.Aspect, f.Opponent!.Value.Head)));
        Console.WriteLine($"| {at:0.0} | {beat} | {(taken ? "yes" : "no (too soon / weaker)")} | {peak:0.00} | {(taken && rise != null ? (rise.T - at).ToString("0.00") : "–")} | " +
                          $"{(taken && over != null ? (over.T - at).ToString("0.00") : taken ? "runs into the next" : "–")} | {Dist(peakFrame) / Dist(before):0.00} | {peakFrame.Out.Fov / before.Out.Fov:0.00} | {Pct(heads, window.Count)} |");
    }
}

// ---- the three framings and the two cameras the user has today, over the ordinary duels ----
Console.WriteLine();
Console.WriteLine("== framings and baselines over the ordinary duels (after the first 1.5 s of each)");
Console.WriteLine("| camera | both in % | heads in % | opposite sides % | separation p50 | player size p50 | opponent size p50 | \"forward\" off the way to the opponent ° mean | view °/s p99 |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
var summaries = new Dictionary<string, (float Both, float Heads, float Player, float Opponent, float Separation, float Opposite)>();
foreach (var (label, framing, camera) in new[]
         {
             ("Duel, Tight", DuelFraming.Tight, Camera.Duel), ("Duel, Standard", DuelFraming.Standard, Camera.Duel), ("Duel, Wide", DuelFraming.Wide, Camera.Duel),
             ("Game camera, pulled back (6 m, yaw untouched)", DuelFraming.Standard, Camera.GameFar),
             ("Game camera, close (2.5 m, yaw untouched)", DuelFraming.Standard, Camera.GameClose),
         })
{
    int two = 0, both = 0, heads = 0, opposite = 0, pairs = 0;
    var separation = new List<float>();
    var player = new List<float>();
    var opponent = new List<float>();
    var off = new List<float>();
    var rate = new List<float>();
    foreach (var s in scenarios.Where(x => x.Ordinary))
    {
        var r = Metrics.Measure(s, Runner.Run(s, framing, camera, out var world), world);
        two += r.SettledTwoFrames;
        both += r.SettledBothIn;
        heads += r.SettledHeadsIn;
        opposite += r.OppositeSides;
        pairs += r.Separation.Count;
        separation.AddRange(r.Separation);
        player.AddRange(r.PlayerSize);
        opponent.AddRange(r.OpponentSize);
        off.AddRange(r.ForwardOffOpponent);
        rate.AddRange(r.ViewRate);
        if (!r.Finite) failures += Fail($"{label}: {s.Name}: output not finite");
    }
    summaries[label] = (100f * both / two, 100f * heads / two, Metrics.Percentile(player, 0.5f), Metrics.Percentile(opponent, 0.5f), Metrics.Percentile(separation, 0.5f), 100f * opposite / Math.Max(1, pairs));
    Console.WriteLine($"| {label} | {Pct(both, two)} | {Pct(heads, two)} | {Pct(opposite, pairs)} | {P(separation, 0.5f)} | {P(player, 0.5f)} | {P(opponent, 0.5f)} | {(off.Count > 0 ? off.Average() : 0f):0} | {P(rate, 0.99f, "0")} |");
}

// ---- closed loop: the player moves by the yaw the camera gives the game ----
Console.WriteLine();
Console.WriteLine("== controls: the scripted player holds a key and moves by the yaw given to the game (read one frame late), 5 m/s");
Console.WriteLine("| yaw given to the game | hold W from 12 m: reaches 2.2 m after s (straight 1.96) | path m (straight 9.8) | hold D from 3 m, 10 s: distance to the opponent m | turns round them | camera turn °/s p50/max | fighters on opposite sides % | line crossings | forward off the way to the opponent ° mean | off the rendered yaw ° mean/max |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
foreach (var (label, lean) in new[] { ("the rendered yaw, leaned towards the opponent (as built)", (float?)null), ("the rendered yaw alone", 0f) })
{
    var w = scenarios.First(x => x.Name.StartsWith("hold W"));
    var frames = Runner.Run(w, DuelFraming.Standard, Camera.Duel, out var ww, lean);
    var walked = Metrics.Measure(w, frames, ww);
    var reached = frames.FirstOrDefault(f => f.Opponent is { } o && Vector3.Distance(f.Player.Feet, o.Feet) <= 2.25f);
    var path = 0f;
    for (var i = 1; i < frames.Count; i++) path += Vector3.Distance(frames[i].Player.Feet, frames[i - 1].Player.Feet);

    var d = scenarios.First(x => x.Name.StartsWith("hold D"));
    frames = Runner.Run(d, DuelFraming.Standard, Camera.Duel, out var dw, lean);
    var seps = frames.Where(f => f.Opponent != null && f.T > 1f).Select(f => Vector3.Distance(f.Player.Feet, f.Opponent!.Value.Feet)).ToList();
    var turned = 0f;
    for (var i = 1; i < frames.Count; i++)
        turned += DeathShotMath.AngleDelta(DeathShotMath.Azimuth(frames[i - 1].Player.Feet), DeathShotMath.Azimuth(frames[i].Player.Feet));
    var held = Metrics.Measure(d, frames, dw);
    Console.WriteLine($"| {label} | {(reached != null ? reached.T.ToString("0.00") : "never")} | {path:0.0} | {seps.Min():0.0}..{seps.Max():0.0} | {MathF.Abs(turned) / 360f / DeathShotMath.Deg:0.0} | " +
                      $"{P(held.ViewRate, 0.5f, "0")}/{Max(held.ViewRate)} | {Pct(held.OppositeSides, held.Separation.Count)} | {held.LineCrossings} | {walked.ForwardOffOpponent.Average():0} | {held.ViewVsMove.Average():0}/{held.ViewVsMove.Max():0} |");
    if (lean == null)
    {
        if (reached == null || reached.T > 2.3f) failures += Fail("hold W does not bring the player straight to the opponent");
        if (seps.Min() < 2.5f || seps.Max() > 3.6f) failures += Fail($"hold D is not a circle round the opponent ({seps.Min():0.0}..{seps.Max():0.0} m)");
        if (held.HeadsIn < held.TwoFrames * 0.99f) failures += Fail("hold D loses a head");
        if (held.ViewVsMove.Max() > DuelCameraSolver.DefaultMoveLean / DeathShotMath.Deg + 0.5f) failures += Fail($"the yaw given to the game is {held.ViewVsMove.Max():0.0}° off the rendered one");
    }
}

// ---- gates ----
Console.WriteLine();
Console.WriteLine("== gates");
foreach (var (s, r) in results)
{
    if (!r.Finite) failures += Fail($"{s.Name}: output not finite");
    if (r.MinGround < DuelCameraSolver.GroundHardFloor - 1e-3f) failures += Fail($"{s.Name}: eye {r.MinGround:0.000} m above the ground");
    if (r.BehindWall > 0) failures += Fail($"{s.Name}: the eye is behind a wall on {r.BehindWall} frame(s)");
    if (r.ViewRate.Count > 0 && r.ViewRate.Max() > DuelCameraSolver.ViewCap / DeathShotMath.Deg * 1.02f) failures += Fail($"{s.Name}: view turns at {r.ViewRate.Max():0}°/s");
    if (r.LineCrossings > s.CrossingsAllowed) failures += Fail($"{s.Name}: the eye crossed the line between the fighters {r.LineCrossings} time(s) (allowed {s.CrossingsAllowed})");
    if (r.Cuts > 0) failures += Fail($"{s.Name}: {r.Cuts} cut(s)");
    if (r.TwoFrames > 0 && r.MinToOpponent < 0.3f) failures += Fail($"{s.Name}: the eye comes within {r.MinToOpponent:0.00} m of the opponent's body");
    if (r.MinToPlayer < (world0(s) ? 0.3f : 0.6f)) failures += Fail($"{s.Name}: the eye comes within {r.MinToPlayer:0.00} m of the player");
    if (s.DashEnds.Length > 0 && r.MaxRecovery > 1.5f) failures += Fail($"{s.Name}: {r.MaxRecovery:0.00} s to get back to the two-shot after a dash");
    var yawStepCap = (DuelCameraSolver.ViewCap + DuelCameraSolver.MoveLeanCap) / DeathShotMath.Deg / s.Fps * 1.15f;
    if (r.MoveYawError.Max() > yawStepCap) failures += Fail($"{s.Name}: the movement yaw steps {r.MoveYawError.Max():0.0}° in a frame (the caps allow {yawStepCap:0.0}°)");
    if (r.ViewVsMove.Max() > DuelCameraSolver.DefaultMoveLean / DeathShotMath.Deg + 0.5f) failures += Fail($"{s.Name}: the movement yaw is {r.ViewVsMove.Max():0.0}° off the rendered yaw");
    if (r.MaxSwitch > 1.5f) failures += Fail($"{s.Name}: {r.MaxSwitch:0.00} s from a change of opponent to both heads in the picture");
    if (s.Ordinary && r.SettledTwoFrames > 0)
    {
        if (100f * r.SettledHeadsIn / r.SettledTwoFrames < 99f) failures += Fail($"{s.Name}: both heads in the picture only {Pct(r.SettledHeadsIn, r.SettledTwoFrames)} % of the time");
        if (100f * r.SettledBothIn / r.SettledTwoFrames < 95f) failures += Fail($"{s.Name}: both fighters in the picture only {Pct(r.SettledBothIn, r.SettledTwoFrames)} % of the time");
    }
    if (!s.Ordinary && r.TwoFrames > 0 && 100f * r.HeadsIn / r.TwoFrames < 90f) failures += Fail($"{s.Name}: both heads in the picture only {Pct(r.HeadsIn, r.TwoFrames)} % of the time");
    if (r.PlayerIn < r.Frames * (s.Ordinary ? 0.99f : 0.95f)) failures += Fail($"{s.Name}: the player in the picture only {Pct(r.PlayerIn, r.Frames)} % of the time");
}
{
    // Against what the user has today: the two-shot must beat the pulled-back camera on presence and the close one on
    // keeping the opponent in the picture.
    var duel = summaries["Duel, Standard"];
    var far = summaries["Game camera, pulled back (6 m, yaw untouched)"];
    var close = summaries["Game camera, close (2.5 m, yaw untouched)"];
    if (duel.Both < MathF.Max(far.Both, close.Both)) failures += Fail($"both fighters in the picture {duel.Both:0.0} %, a game camera does {MathF.Max(far.Both, close.Both):0.0} %");
    if (duel.Player < far.Player * 1.3f) failures += Fail($"the player is no larger than with the pulled-back camera ({duel.Player:0.00} against {far.Player:0.00})");
    if (duel.Opponent < far.Opponent) failures += Fail($"the opponent is smaller than with the pulled-back camera ({duel.Opponent:0.00} against {far.Opponent:0.00})");
    if (duel.Opposite < 90f) failures += Fail($"the fighters are on opposite sides of the picture only {duel.Opposite:0.0} % of the time");
}
Console.WriteLine(failures == 0 ? "== ALL PASS" : $"== {failures} FAILURE(S)");
return failures == 0 ? 0 : 1;

static int Fail(string what)
{
    Console.WriteLine("  FAIL  " + what);
    return 1;
}

// A duel with walls may bring the eye over the player's head (their back to a wall); elsewhere it keeps its distance.
static bool world0(Scenario s) => s.World().Walls.Count > 0;
