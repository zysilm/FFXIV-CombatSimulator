// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Animation;
using CombatSimulator.Core;

int checks = 0;
void Check(bool condition, string reason) { checks++; if (!condition) throw new Exception(reason); }

// Weighted selection.
var weights = new[] { 3f, 1f };
Check(WeightedPick.Pick(weights, 0.0) == 0 && WeightedPick.Pick(weights, 0.74) == 0 && WeightedPick.Pick(weights, 0.76) == 1,
    "Roulette boundaries must follow the weights");
Check(WeightedPick.Pick(weights, 1.0) == 1 && WeightedPick.Pick(weights, double.NaN) == 0,
    "Out-of-range rolls must clamp instead of failing");
Check(WeightedPick.Pick(new[] { 0f, float.NaN, -1f, float.PositiveInfinity }, 0.5) == -1,
    "Zero, negative and non-finite weights must never be chosen");
Check(WeightedPick.Pick(new[] { 0f, 2f, 0f }, 0.99) == 1, "Zero-weight neighbours must be skipped");
for (var r = 0.0; r < 1.0; r += 0.05)
    Check(WeightedPick.Pick(new[] { 1f, 1f, 1f }, r, avoid: 1) != 1, "The avoided pick must not repeat when others exist");
Check(WeightedPick.Pick(new[] { 0f, 5f }, 0.3, avoid: 1) == 1, "A single usable candidate must still be returned");
Check(WeightedPick.Pick(Array.Empty<float>(), 0.3) == -1, "An empty pool picks nothing");
var counts = new int[3];
var rng = new Random(7);
for (var i = 0; i < 30000; i++) counts[WeightedPick.Pick(new[] { 6f, 3f, 1f }, rng.NextDouble())]++;
Check(Math.Abs(counts[0] / 30000.0 - 0.6) < 0.02 && Math.Abs(counts[2] / 30000.0 - 0.1) < 0.02,
    "Long-run frequencies must match the weights");

// Pools.
foreach (var mood in Enum.GetValues<CelebrationMood>())
{
    var pool = VictoryCelebrationPlan.PoolFor(mood);
    Check(pool.Count >= 3 && pool.All(e => e.EmoteId != 0 && e.Weight > 0f), "Every mood needs a usable varied pool");
    Check(pool.Select(e => e.EmoteId).Distinct().Count() == pool.Count, "Pools must not list an emote twice");
    var (hmin, hmax) = VictoryCelebrationPlan.Interval(mood, humanoid: true);
    var (mmin, mmax) = VictoryCelebrationPlan.Interval(mood, humanoid: false);
    Check(hmin > 1f && hmax >= hmin && mmin > 1f && mmax >= mmin, "Replays must be spaced out, never every frame");
}
Check(!VictoryCelebrationPlan.BystanderEmotes.Any(b => VictoryCelebrationPlan.VictoryEmotes.Any(v => v.EmoteId == b.EmoteId && v.EmoteId != 121)),
    "Bystander idles must stay low-key, not reuse the celebration set");
Check(VictoryCelebrationPlan.BoundedStagger(float.NaN) == 0f && VictoryCelebrationPlan.BoundedStagger(-1f) == 0f &&
      VictoryCelebrationPlan.BoundedStagger(99f) == VictoryCelebrationPlan.MaxStaggerSeconds,
    "Saved stagger values must stay finite and bounded");

// Cue timing.
var cue = new CelebrationCue(1f, 6f, 10f);
Check(!cue.Advance(0.25f, 0.5) && !cue.Advance(0.25f, 0.5) && !cue.Advance(0.25f, 0.5) && !cue.Advance(0.2f, 0.5),
    "The first play must wait for its stagger");
Check(cue.Advance(0.1f, 0.5) && cue.Plays == 1, "The stagger deadline must fire exactly once");
Check(!cue.Advance(0.1f, 0.5), "A play must not repeat on the next frame");
var fired = 0;
for (var i = 0; i < 1000; i++) if (cue.Advance(0.1f, 0.0)) fired++;
Check(fired >= 9 && fired <= 17, "Replays must follow the interval range");
var hitch = new CelebrationCue(0f, 6f, 10f);
Check(hitch.Advance(30f, 0.5) && !hitch.Advance(0.01f, 0.5), "A hitch must fire at most once");
var immediate = new CelebrationCue(float.NaN, float.NaN, -5f);
Check(immediate.Advance(0f, 0.5), "Invalid inputs must degrade to an immediate, bounded cue");
Check(!immediate.Advance(0.25f, double.NaN), "Invalid intervals must still be at least half a second");
var staggers = Enumerable.Range(0, 8).Select(i => new CelebrationCue(i * 0.2f, 6f, 10f)).ToList();
var firstFrames = staggers.Select(c => { var f = 0; while (!c.Advance(1f / 60f, 0.5)) f++; return f; }).ToList();
Check(firstFrames.Distinct().Count() == firstFrames.Count, "Staggered cues must start on different frames");

Console.WriteLine($"Passed {checks} victory celebration checks.");
