// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Immutable;

namespace CombatSimulator.Animation.SkeletonProfiles;

/// <summary>How a <see cref="BoneNamePattern"/> compares a bone name. Every comparison is ordinal.</summary>
public enum BoneNameMatch
{
    /// <summary>The whole name equals the text.</summary>
    Exact,
    /// <summary>The name starts with the text.</summary>
    Prefix,
    /// <summary>The text occurs anywhere in the name.</summary>
    Contains,
}

/// <summary>One test on a bone name.</summary>
public readonly record struct BoneNamePattern(BoneNameMatch Match, string Text)
{
    public bool Matches(string name) => Match switch
    {
        BoneNameMatch.Exact => name == Text,
        BoneNameMatch.Prefix => name.StartsWith(Text, StringComparison.Ordinal),
        _ => name.Contains(Text, StringComparison.Ordinal),
    };
}

/// <summary>
/// A set of bone names a profile groups together ("the hand bones", "cloth bones"), as a list of
/// patterns: a name belongs to the class when any pattern matches it. Immutable.
/// </summary>
public sealed class BoneNameClass
{
    private readonly BoneNamePattern[] patterns;

    public BoneNameClass(params BoneNamePattern[] patterns)
        => this.patterns = (BoneNamePattern[])patterns.Clone();

    /// <summary>A class no name belongs to.</summary>
    public static BoneNameClass None { get; } = new();

    public ImmutableArray<BoneNamePattern> Patterns => ImmutableArray.Create(patterns);

    public bool Matches(string name)
    {
        foreach (var pattern in patterns)
            if (pattern.Matches(name))
                return true;
        return false;
    }

    public static BoneNameClass Exact(params string[] names) => Of(BoneNameMatch.Exact, names);
    public static BoneNameClass Prefix(params string[] prefixes) => Of(BoneNameMatch.Prefix, prefixes);
    public static BoneNameClass Contains(params string[] parts) => Of(BoneNameMatch.Contains, parts);

    /// <summary>The names of this class and of <paramref name="other"/>.</summary>
    public BoneNameClass Or(BoneNameClass other)
    {
        var merged = new BoneNamePattern[patterns.Length + other.patterns.Length];
        patterns.CopyTo(merged, 0);
        other.patterns.CopyTo(merged, patterns.Length);
        return new BoneNameClass(merged);
    }

    private static BoneNameClass Of(BoneNameMatch match, string[] texts)
    {
        var result = new BoneNamePattern[texts.Length];
        for (var i = 0; i < texts.Length; i++)
            result[i] = new BoneNamePattern(match, texts[i]);
        return new BoneNameClass(result);
    }
}

/// <summary>One entry of an ordered rule list: the first rule whose class matches a name gives its value.</summary>
public readonly record struct BoneRule<T>(BoneNameClass Class, T Value);

/// <summary>Helpers for ordered rule lists.</summary>
public static class BoneRules
{
    /// <summary>The value of the first rule whose class matches <paramref name="name"/>.</summary>
    public static bool TryFirst<T>(ImmutableArray<BoneRule<T>> rules, string name, out T value)
    {
        foreach (var rule in rules)
        {
            if (rule.Class.Matches(name))
            {
                value = rule.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }
}
