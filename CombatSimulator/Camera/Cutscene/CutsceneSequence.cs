// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CombatSimulator.Camera.Cutscene;

/// <summary>
/// How a timeline's camera shots combine into what is actually seen (the default sequence). Shots live on TMTR tracks
/// (owned by one camera TMAC); a track may carry a Lua condition (variants); C004 entries on no track are never
/// referenced. Within the unconditional tracks a later track overrides an earlier one while its shot is active
/// ("last track wins"). Checked offline by coverage (Tests/CutsceneCamera `bind N seq`): the resolved sequence covers
/// 92.8% of timelines completely. Game-free (also compiled by Tests/CutsceneCamera).
/// </summary>
public static class CutsceneSequence
{
    public sealed record Resolved(TmbTimeline Tl, List<(int From, int To, CameraShot Shot)> Spans, int Covered);

    public static Resolved Resolve(CutbFile c, TmbTimeline tl, Func<string, bool>? acceptCondition = null)
    {
        var shots = CameraShot.FromTimeline(c, tl).ToList();
        var byId = tl.Items.Where(i => i.Magic == "C004").Zip(shots).ToList(); // same order as FromTimeline
        var tracks = byId.Where(p => p.First.OwnerTrack >= 0)
            .GroupBy(p => p.First.OwnerTrack)
            .Where(g => !tl.TrackLua.TryGetValue(g.Key, out var lua) || (acceptCondition?.Invoke(TmbLua.Text(lua)) ?? false))
            .OrderBy(g => g.Key)
            .ToList();
        var owner = new CameraShot?[Math.Max(tl.Length, 1)];
        foreach (var g in tracks)
            foreach (var (_, s) in g)
                for (var f = Math.Max(0, s.Start); f < Math.Min(owner.Length, s.Start + s.Duration); f++)
                    owner[f] = s;
        var spans = new List<(int, int, CameraShot)>();
        for (var f = 0; f < owner.Length;)
        {
            if (owner[f] == null) { f++; continue; }
            var g = f;
            while (g < owner.Length && owner[g] == owner[f]) g++;
            spans.Add((f, g, owner[f]!));
            f = g;
        }
        return new Resolved(tl, spans, owner.Count(o => o != null));
    }
}

/// <summary>Lua condition tokens of a TMTR track as readable text. Variable labels for pool 1 are the community names
/// VFXEditor (MIT) lists; only the few cutscene ones are repeated here.</summary>
public static class TmbLua
{
    private static readonly Dictionary<int, string> Pool1 = new()
    {
        [0x00] = "GameLanguage", [0x01] = "CaptionLanguage", [0x02] = "VoiceLanguage", [0x03] = "SkeletonId",
        [0x04] = "StartingTown", [0x09] = "QuestProgression", [0x0E] = "DialogChoice", [0x10] = "LegacyPlayer",
        [0x12] = "ClassJob", [0x1B] = "Gender", [0x23] = "IsGatherer", [0x24] = "IsCrafter",
    };

    public static string Text(List<(int Op, uint Value, float F)> toks) => string.Join(" ", toks.Select(t => t.Op switch
    {
        0x13 => (t.Value >> 28) == 1 && Pool1.TryGetValue((int)(t.Value & 0x0FFFFFFF), out var n) ? n : $"var{t.Value >> 28}:{t.Value & 0x0FFFFFFF:X}",
        0x11 => ((int)t.Value).ToString(),
        0x12 => t.F.ToString("G4"),
        0x0D => "==", 0x0C => "!=", 0x0E => "&&", 0x0F => "||", 0x10 => "!", 0x08 => ">", 0x09 => ">=", 0x0A => "<", 0x0B => "<=",
        0x01 => new string('(', (int)Math.Clamp(t.Value, 1, 9)), 0x02 => new string(')', (int)Math.Clamp(t.Value, 1, 9)),
        0x14 => "true", 0x15 => "false",
        _ => $"op{t.Op:X}",
    }));
}
