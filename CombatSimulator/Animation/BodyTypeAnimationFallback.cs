// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CombatSimulator.Animation;

/// <summary>
/// Emote animations for bodies that lack them. Only the adult male set (c0101) carries every
/// emote; the young and elder bodies (race codes ending in 03/04) are missing most of them, so a
/// character of such a body plays nothing when an emote timeline is set on it. Before an emote is
/// played on such a character, the timeline's animation file path for its own body is redirected
/// to the adult male file through a Penumbra temporary mod (a game-path swap, applied to every
/// collection), exactly as mods that bring emotes to those bodies do. Without Penumbra, or when the
/// adult file is missing too, nothing changes and the reason is logged once.
/// </summary>
public static unsafe class BodyTypeAnimationFallback
{
    private const string ModTag = "CombatSimulator.BodyTypeAnimations";
    private const int Priority = 0;
    private const string AdultMaleCode = "c0101";
    private static IDalamudPluginInterface? pluginInterface;
    private static IDataManager? dataManager;
    private static IPluginLog? log;
    private static readonly Dictionary<string, string> redirects = new(StringComparer.Ordinal);
    private static readonly HashSet<(ushort Race, ushort Timeline)> considered = new();
    private static readonly Dictionary<ushort, string> timelineKeys = new();
    private static bool registered, warnedNoPenumbra;

    public static void Initialize(IDalamudPluginInterface pi, IDataManager data, IPluginLog pluginLog)
    {
        pluginInterface = pi; dataManager = data; log = pluginLog;
    }

    /// <summary>
    /// Makes sure <paramref name="timelines"/> can play on <paramref name="character"/>'s body:
    /// adds the game-path swaps the body needs (idempotent, cheap after the first call per
    /// body and timeline). Call before setting a timeline; a swap is honoured when the file is
    /// next loaded.
    /// </summary>
    public static void EnsureEmoteFiles(Character* character, params ushort[] timelines)
    {
        if (character == null || dataManager == null) return;
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->GetObjectType() != ObjectType.CharacterBase) return;
        var characterBase = (CharacterBase*)drawObject;
        if (characterBase->GetModelType() != CharacterBase.ModelType.Human) return;
        ushort race = ((Human*)characterBase)->RaceSexId;
        byte bodyType = ((Human*)characterBase)->Customize.BodyType;
        // Standard adult bodies (..01) have their own files or the game's own fallbacks.
        int body = race % 100;
        bool changed = false;
        foreach (var timeline in timelines)
        {
            if (timeline == 0 || !considered.Add((race, timeline))) continue;
            var key = TimelineKey(timeline);
            string own = $"chara/human/c{race:D4}/animation/a0001/bt_common/{key}.pap";
            string adult = $"chara/human/{AdultMaleCode}/animation/a0001/bt_common/{key}.pap";
            bool ownExists = false, adultExists = false;
            try { if (key.Length > 0) { ownExists = dataManager.FileExists(own); adultExists = dataManager.FileExists(adult); } }
            catch (Exception ex) { log?.Warning(ex, $"Body-type animation fallback: cannot check {own}"); continue; }
            log?.Info($"Body-type animation fallback: body c{race:D4} (BodyType {bodyType}) timeline {timeline} '{key}': own file {(ownExists ? "present" : "missing")}, adult file {(adultExists ? "present" : "missing")}");
            if (body is not (3 or 4) || key.Length == 0 || ownExists || !adultExists) continue;
            redirects[own] = adult;
            changed = true;
            log?.Info($"Body-type animation fallback: {own} -> {adult} (timeline {timeline} '{key}')");
        }
        if (changed) Register();
    }

    private static string TimelineKey(ushort timeline)
    {
        if (timelineKeys.TryGetValue(timeline, out var cached)) return cached;
        string key = "";
        try
        {
            var row = dataManager?.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>()?.GetRowOrDefault(timeline);
            if (row.HasValue) key = row.Value.Key.ExtractText().Trim();
        }
        catch (Exception ex) { log?.Warning(ex, $"Body-type animation fallback: cannot read timeline {timeline}"); }
        timelineKeys[timeline] = key;
        return key;
    }

    /// <summary>Replaces the temporary mod with the current swap set (the tag identifies it).</summary>
    private static void Register()
    {
        if (pluginInterface == null || redirects.Count == 0) return;
        try
        {
            if (registered)
            {
                var remove = pluginInterface.GetIpcSubscriber<string, int, int>("Penumbra.RemoveTemporaryModAll.V5");
                remove.InvokeFunc(ModTag, Priority);
                registered = false;
            }
            // Penumbra.AddTemporaryModAll.V5 : (tag, {gamePath -> replacement}, manipString, priority) -> ec(0=ok).
            // An unrooted replacement is another game path: a swap, no file on disk.
            var add = pluginInterface.GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryModAll.V5");
            int ec = add.InvokeFunc(ModTag, new Dictionary<string, string>(redirects, StringComparer.Ordinal), string.Empty, Priority);
            registered = ec == 0;
            if (ec != 0) log?.Warning($"Body-type animation fallback: Penumbra AddTemporaryModAll returned {ec}");
        }
        catch (Exception ex)
        {
            if (!warnedNoPenumbra)
            {
                warnedNoPenumbra = true;
                log?.Warning($"Body-type animation fallback unavailable (Penumbra IPC): {ex.Message}");
            }
        }
    }

    public static void Shutdown()
    {
        try
        {
            if (registered && pluginInterface != null)
                pluginInterface.GetIpcSubscriber<string, int, int>("Penumbra.RemoveTemporaryModAll.V5").InvokeFunc(ModTag, Priority);
        }
        catch (Exception ex) { log?.Debug($"Body-type animation fallback: removal skipped ({ex.Message})"); }
        registered = false;
        redirects.Clear(); considered.Clear(); timelineKeys.Clear();
        pluginInterface = null; dataManager = null; log = null;
    }
}
