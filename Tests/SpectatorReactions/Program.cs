// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Core;
using CombatSimulator.Spectators;

int checks = 0;
void Check(bool condition, string reason) { checks++; if (!condition) throw new Exception(reason); }

// Outcome-aware chatter pool.
foreach (var friendly in new[] { false, true })
foreach (var enemy in new[] { false, true })
    Check(SpectatorReactionPlan.ResolveOutcomePool(false, friendly, enemy, true, true) == SpectatorOutcomePool.Decisive,
        "With the flag off the decisive pool must be used exactly as before");
Check(SpectatorReactionPlan.ResolveOutcomePool(true, true, false, true, true) == SpectatorOutcomePool.Victory,
    "Player side standing, enemies down: victory pool");
Check(SpectatorReactionPlan.ResolveOutcomePool(true, false, true, true, true) == SpectatorOutcomePool.Defeat,
    "Player side down, enemy standing: defeat pool");
Check(SpectatorReactionPlan.ResolveOutcomePool(true, false, false, true, true) == SpectatorOutcomePool.Decisive,
    "A mutual wipe has no winner");
Check(SpectatorReactionPlan.ResolveOutcomePool(true, true, false, false, true) == SpectatorOutcomePool.Decisive &&
      SpectatorReactionPlan.ResolveOutcomePool(true, false, true, true, false) == SpectatorOutcomePool.Decisive,
    "An empty outcome pool must fall back to the decisive pool instead of going silent");

// Reaction pools.
foreach (var reaction in Enum.GetValues<SpectatorReaction>())
{
    var pool = SpectatorReactionPlan.Pool(reaction);
    Check(pool.Count >= 3 && pool.All(e => e.EmoteId != 0 && e.Weight > 0f), $"{reaction} needs a varied usable pool");
    Check(WeightedPick.Pick(pool, e => e.Weight, 0.5) >= 0, $"{reaction} must always yield a pick");
}
var victoryIds = SpectatorReactionPlan.Pool(SpectatorReaction.PlayerVictory).Select(e => e.EmoteId).ToHashSet();
var defeatIds = SpectatorReactionPlan.Pool(SpectatorReaction.PlayerDefeat).Select(e => e.EmoteId).ToHashSet();
Check(!victoryIds.Contains(32) && !defeatIds.Contains(6) && !defeatIds.Contains(7),
    "A defeat must never be applauded and a win must not read as shock");
Check(SpectatorReactionPlan.ResponseChance is > 0 and < 1 && SpectatorReactionPlan.MaxResponseDelay > 0f,
    "Reactions must be partial and staggered");
Check(SpectatorReactionPlan.CreateDefaultVictoryLines().Count > 0 && SpectatorReactionPlan.CreateDefaultDefeatLines().Count > 0 &&
      SpectatorReactionPlan.CreateDefaultVictoryLines().All(l => l.Length is > 0 and <= 180),
    "Default outcome lines must be present and within the bubble limit");
Check(!ReferenceEquals(SpectatorReactionPlan.CreateDefaultVictoryLines(), SpectatorReactionPlan.CreateDefaultVictoryLines()),
    "Defaults must be fresh lists so config edits cannot mutate them");

Console.WriteLine($"Passed {checks} spectator reaction checks.");
