using System;
using System.Collections.Generic;

namespace CombatSimulator.Dev;

public enum MonsterStrikePartProfile { Off = 0, Sequential = 1, Random = 2 }

/// <summary>
/// Decides which body part comes apart on each connecting monster strike, for the two profiles.
/// Sequential peels the lower pieces (fore-arms / shins) first, then the upper pieces (upper-arms /
/// thighs), then the head; Random picks any still-attached piece. Both respect the cut hierarchy:
/// once an upper piece is gone its lower descendant is already gone with it, so a child is never
/// selected after its parent (a parent after a child is allowed — that is a clean boundary cut).
/// </summary>
public sealed class MonsterDismembermentPlanner
{
    public const string Head = "j_kao";

    // Lower pieces (fore-arms + shins) and the upper pieces (upper-arms + thighs) that supersede them.
    private static readonly string[] Lower = { "j_ude_b_l", "j_ude_b_r", "j_asi_b_l", "j_asi_b_r" };
    private static readonly string[] Upper = { "j_ude_a_l", "j_ude_a_r", "j_asi_a_l", "j_asi_a_r" };

    // child -> parent: selecting the parent (upper) supersedes the child (lower).
    private static readonly Dictionary<string, string> ParentOf = new(StringComparer.Ordinal)
    {
        ["j_ude_b_l"] = "j_ude_a_l",
        ["j_ude_b_r"] = "j_ude_a_r",
        ["j_asi_b_l"] = "j_asi_a_l",
        ["j_asi_b_r"] = "j_asi_a_r",
    };

    private readonly List<string> severed = new();
    private readonly Random rng = new();

    /// <summary>The accumulated set severed so far (drives the dismemberment selection).</summary>
    public IReadOnlyList<string> Severed => severed;

    public void Reset() => severed.Clear();

    /// <summary>Pick the next part to sever for this connecting hit, or null when nothing remains.
    /// The chosen bone is appended to <see cref="Severed"/>.</summary>
    public string? NextHit(MonsterStrikePartProfile profile)
    {
        var bone = profile switch
        {
            MonsterStrikePartProfile.Sequential => NextSequential(),
            MonsterStrikePartProfile.Random => NextRandom(),
            _ => null,
        };
        if (bone != null)
            severed.Add(bone);
        return bone;
    }

    private string? NextSequential()
    {
        var lower = Remaining(Lower);
        if (lower.Count > 0) return lower[rng.Next(lower.Count)];
        var upper = Remaining(Upper);
        if (upper.Count > 0) return upper[rng.Next(upper.Count)];
        return severed.Contains(Head) ? null : Head;
    }

    private string? NextRandom()
    {
        var candidates = new List<string>();
        if (!severed.Contains(Head)) candidates.Add(Head);
        foreach (var b in Upper) if (IsAvailable(b)) candidates.Add(b);
        foreach (var b in Lower) if (IsAvailable(b)) candidates.Add(b);
        return candidates.Count == 0 ? null : candidates[rng.Next(candidates.Count)];
    }

    private List<string> Remaining(string[] set)
    {
        var list = new List<string>();
        foreach (var b in set)
            if (!severed.Contains(b)) list.Add(b);
        return list;
    }

    // Available = not already severed and not already covered by a severed ancestor.
    private bool IsAvailable(string bone)
    {
        if (severed.Contains(bone)) return false;
        return !ParentOf.TryGetValue(bone, out var parent) || !severed.Contains(parent);
    }
}
