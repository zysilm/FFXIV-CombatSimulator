namespace CutsceneCamera;

/// <summary>How the shots of one cutscene are organised: per timeline, which tracks hold camera shots and the Lua
/// condition (if any) that gates each track. Variable labels for pool 1 are the community names VFXEditor (MIT)
/// lists; only the few cutscene ones are repeated here.</summary>
public static class Branches
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

    public static string? ShotCondition(TmbTimeline tl, int shotEntryId)
    {
        var it = tl.ById[shotEntryId];
        return it.OwnerTrack >= 0 && tl.TrackLua.TryGetValue(it.OwnerTrack, out var toks) ? Text(toks) : null;
    }

    public static void Print(CutbFile c)
    {
        foreach (var tl in c.Timelines)
        {
            var shots = tl.Items.Where(i => i.Magic == "C004").ToList();
            var luaTracks = tl.TrackLua.Count;
            Console.WriteLine($"CTTL#{tl.Index} len {tl.Length} shots {shots.Count} lua-tracks {luaTracks}" +
                (tl.TrackLua.Count > 0 ? "  conditions: " + string.Join(" | ", tl.TrackLua.Values.Select(Text).Distinct().Take(4)) : ""));
            foreach (var g in shots.GroupBy(s => s.OwnerTrack))
            {
                var cond = g.Key >= 0 && tl.TrackLua.TryGetValue(g.Key, out var t) ? Text(t) : "(always)";
                Console.WriteLine($"   track {g.Key} actor {g.First().OwnerActor}: {cond} → " + string.Join(" ", g.Select(s => $"[{s.Time}+{s.Word(c.Data, 0)}]")));
            }
        }
    }
}
