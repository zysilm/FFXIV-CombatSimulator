// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace CombatSimulator.Voice;

/// <summary>Combat moments an enemy may voice.</summary>
public enum VoiceEvent
{
    Engage,
    Skill,
    Hurt,
    LowHp,
    KilledPlayer,
    Death,
    AllyDeath,
    Victory,
    Taunt,
    /// <summary>Idle remark in a lull (a musing or grumble, no provocation). Only the idle-talk timer
    /// raises it; it never answers a kill, a victory or a taunt over the fallen.</summary>
    Chatter,
}

/// <summary>
/// What a sound entry is, as recorded by the owner in the audition panel. Entries without a label
/// are never played: a guessed entry is as likely to be a death rattle as a war cry.
/// </summary>
public static class VoiceLabels
{
    public const string Engage = "engage";
    public const string Attack = "attack";
    public const string Hurt = "hurt";
    public const string Death = "death";
    public const string Victory = "victory";
    public const string Taunt = "taunt";

    public static readonly string[] All = { Engage, Attack, Hurt, Death, Victory, Taunt };
}

/// <summary>Static per-event policy: priority, cooldowns and which labels may voice it.</summary>
public static class VoiceEventPolicy
{
    /// <summary>
    /// Higher wins when several events compete in one frame or for the last free voice.
    /// Order from the design: death > engage > low-HP > skill > hurt > taunt; the outcome
    /// events (killing blow, victory) sit just under death because they happen once per fight.
    /// </summary>
    public static int Priority(VoiceEvent e) => e switch
    {
        VoiceEvent.Death => 100,
        VoiceEvent.KilledPlayer => 95,
        VoiceEvent.Victory => 90,
        VoiceEvent.Engage => 80,
        VoiceEvent.LowHp => 60,
        VoiceEvent.AllyDeath => 50,
        VoiceEvent.Skill => 40,
        VoiceEvent.Hurt => 30,
        VoiceEvent.Taunt => 10,
        VoiceEvent.Chatter => 5,
        _ => 0,
    };

    /// <summary>Events that skip the global token bucket (still bounded by concurrency). They
    /// happen at most once per actor per fight, and dropping them reads as a bug.</summary>
    public static bool IsCritical(VoiceEvent e) => Priority(e) >= Priority(VoiceEvent.Victory);

    /// <summary>Per actor, per event: how long before the same actor may voice it again.</summary>
    public static float Cooldown(VoiceEvent e) => e switch
    {
        VoiceEvent.Engage => 30f,
        VoiceEvent.Skill => 6f,
        VoiceEvent.Hurt => 5f,
        VoiceEvent.LowHp => 60f,
        VoiceEvent.KilledPlayer => 10f,
        VoiceEvent.Death => 5f,
        VoiceEvent.AllyDeath => 12f,
        VoiceEvent.Victory => 10f,
        VoiceEvent.Taunt => 20f,
        VoiceEvent.Chatter => 20f,
        _ => 5f,
    };

    /// <summary>Labels tried in order. A fallback is still an owner-labeled entry of a related
    /// kind (a war cry for a low-HP rally), never an unlabeled one.</summary>
    public static IReadOnlyList<string> LabelChain(VoiceEvent e) => e switch
    {
        VoiceEvent.Engage => new[] { VoiceLabels.Engage, VoiceLabels.Attack },
        VoiceEvent.Skill => new[] { VoiceLabels.Attack },
        VoiceEvent.Hurt => new[] { VoiceLabels.Hurt },
        VoiceEvent.LowHp => new[] { VoiceLabels.Hurt, VoiceLabels.Engage },
        VoiceEvent.KilledPlayer => new[] { VoiceLabels.Victory, VoiceLabels.Taunt, VoiceLabels.Engage },
        VoiceEvent.Death => new[] { VoiceLabels.Death },
        VoiceEvent.AllyDeath => new[] { VoiceLabels.Engage, VoiceLabels.Taunt },
        VoiceEvent.Victory => new[] { VoiceLabels.Victory, VoiceLabels.Taunt },
        VoiceEvent.Taunt => new[] { VoiceLabels.Taunt },
        _ => Array.Empty<string>(),
    };
}

/// <summary>
/// Owner-recorded entry labels, keyed by <see cref="VoiceSource.LabelKey"/> then label name.
/// Built-in labels ship as a resource; user labels (recorded in the audition panel) override
/// them per key+label.
/// </summary>
public sealed class VoiceLabelTable
{
    private readonly Dictionary<string, Dictionary<string, List<int>>> entries = new(StringComparer.OrdinalIgnoreCase);

    public void Clear() => entries.Clear();

    /// <summary>Replace the entries for one key+label (an empty list removes it, so a user can
    /// also un-label a shipped guess).</summary>
    public void Set(string key, string label, IReadOnlyList<int> soundNumbers)
    {
        if (!entries.TryGetValue(key, out var labels))
            entries[key] = labels = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        labels[label] = new List<int>(soundNumbers);
    }

    public void Merge(IReadOnlyDictionary<string, Dictionary<string, List<int>>>? source)
    {
        if (source == null)
            return;
        foreach (var (key, labels) in source)
            foreach (var (label, numbers) in labels)
                Set(key, label, numbers ?? new List<int>());
    }

    public IReadOnlyList<int> Get(string key, string label) =>
        entries.TryGetValue(key, out var labels) && labels.TryGetValue(label, out var numbers)
            ? numbers : Array.Empty<int>();

    /// <summary>Entry to play for an event, or -1 when nothing usable is labeled.</summary>
    public int Pick(string key, VoiceEvent e, int entryCount, Func<int, int> random)
    {
        foreach (var label in VoiceEventPolicy.LabelChain(e))
        {
            var numbers = Get(key, label);
            var valid = 0;
            foreach (var n in numbers)
                if (n >= 0 && (entryCount <= 0 || n < entryCount))
                    valid++;
            if (valid == 0)
                continue;
            var pick = random(valid);
            foreach (var n in numbers)
            {
                if (n < 0 || (entryCount > 0 && n >= entryCount))
                    continue;
                if (pick-- == 0)
                    return n;
            }
        }
        return -1;
    }
}
