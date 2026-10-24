// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;

namespace CombatSimulator.Spectators;

/// <summary>Moments a crowd reacts to once, on top of its looped emote.</summary>
public enum SpectatorReaction
{
    PlayerVictory,
    PlayerDefeat,
    StoryStarted,
    GrabStarted,
}

/// <summary>Which chatter pool the decisive phase speaks from.</summary>
public enum SpectatorOutcomePool
{
    Decisive,
    Victory,
    Defeat,
}

public readonly record struct SpectatorReactionEmote(uint EmoteId, float Weight);

/// <summary>Pure selection rules for outcome-aware chatter and one-shot crowd reactions.</summary>
public static class SpectatorReactionPlan
{
    // Not everyone reacts, and nobody reacts in the same frame: a crowd, not a chorus line.
    public const double ResponseChance = 0.7;
    public const float MaxResponseDelay = 0.9f;

    /// <summary>
    /// The decisive phase splits by who won when outcome chatter is on and the matching pool has
    /// lines; otherwise it stays on the shared decisive pool exactly as before. A mutual wipe has
    /// no winner and also stays decisive.
    /// </summary>
    public static SpectatorOutcomePool ResolveOutcomePool(
        bool outcomeChatter, bool friendlyAlive, bool enemyAlive, bool victoryHasLines, bool defeatHasLines)
    {
        if (!outcomeChatter || friendlyAlive == enemyAlive) return SpectatorOutcomePool.Decisive;
        if (friendlyAlive) return victoryHasLines ? SpectatorOutcomePool.Victory : SpectatorOutcomePool.Decisive;
        return defeatHasLines ? SpectatorOutcomePool.Defeat : SpectatorOutcomePool.Decisive;
    }

    // Emote rows: cheer 6, clap 7, fist pump 195, surprised 1, shocked 32, panic 26, no 24,
    // huh 17, think 39, angry 2. One-shots blended over the spectator's own loop.
    public static IReadOnlyList<SpectatorReactionEmote> Pool(SpectatorReaction reaction) => reaction switch
    {
        SpectatorReaction.PlayerVictory => new SpectatorReactionEmote[] { new(6, 3f), new(7, 3f), new(195, 2f), new(1, 1f) },
        SpectatorReaction.PlayerDefeat => new SpectatorReactionEmote[] { new(32, 3f), new(1, 2f), new(26, 1f), new(24, 1f) },
        SpectatorReaction.StoryStarted => new SpectatorReactionEmote[] { new(1, 2f), new(17, 2f), new(39, 1f) },
        _ => new SpectatorReactionEmote[] { new(32, 3f), new(1, 2f), new(2, 1f) },
    };

    public static List<string> CreateDefaultVictoryLines() => new()
    {
        "They did it!",
        "What a win!",
        "Never doubted them for a second!",
        "That's how it's done!",
        "Somebody buy that one a drink!",
        "Victory!",
    };

    public static List<string> CreateDefaultDefeatLines() => new()
    {
        "Oh no...",
        "They're down!",
        "That's gonna leave a mark.",
        "Somebody help them!",
        "I can't watch...",
        "It's all over for them.",
    };
}
