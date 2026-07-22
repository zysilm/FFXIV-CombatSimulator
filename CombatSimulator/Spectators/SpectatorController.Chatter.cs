using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulator.Core;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace CombatSimulator.Spectators;

/// <summary>
/// Local-only speech bubbles and the optional XivVoices bridge for spectators. The bridge emits a
/// synthetic local chat event and suppresses it before the game can print it. It never executes a
/// chat command or calls a network-facing chat sender.
/// </summary>
public sealed unsafe partial class SpectatorController
{
    public const int MaxChatterConcurrent = 20;
    public const int MaxChatterLines = 100;
    public const int MaxChatterLineLength = 180;

    private const double PendingBridgeLifetimeSeconds = 5d;
    private const byte DefaultBalloonParentBone = 25;

    private readonly Configuration config;
    private readonly List<PendingBridgeMessage> pendingBridgeMessages = new();
    private readonly object pendingBridgeLock = new();

    private double chatterClock;
    private bool chatterWasEnabled;
    private bool chatterBridgeSubscribed;

    public int ActiveChatterCount
        => spectators.Count(actor => actor.ChatterUntil > chatterClock);

    public SpectatorChatterPhase CurrentChatterPhase => ResolveChatterPhase();

    public string LastChatterSpeaker { get; private set; } = string.Empty;
    public string LastChatterText { get; private set; } = string.Empty;
    public string LastChatterError { get; private set; } = string.Empty;

    public bool IsXivVoicesLoaded
    {
        get
        {
            try
            {
                return Services.PluginInterface.InstalledPlugins.Any(plugin =>
                    plugin.IsLoaded &&
                    plugin.InternalName.Equals("XivVoices", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }

    public static List<string> CreateDefaultBattleChatterLines()
        => new()
        {
            "What a hit!",
            "Keep going!",
            "Look out!",
            "Don't let up!",
            "Stay focused!",
            "Watch your flank!",
            "Keep the pressure on!",
            "Now that's a fight!",
            "They're trading blows!",
            "No backing down now!",
            "This could go either way!",
            "Let's see who breaks first!",
        };

    public static List<string> CreateDefaultDecisiveChatterLines()
        => new()
        {
            "It's over!",
            "What a finish!",
            "And that's the fight!",
            "What a decisive blow!",
            "That's a clean finish!",
            "They never saw that coming!",
            "No coming back from that!",
            "That settled it!",
            "What a way to end it!",
            "The crowd has its answer!",
            "Someone finally broke!",
            "What a battle!",
        };

    private void InitializeChatter()
    {
        var configChanged = false;
        if (config.SpectatorBattleChatterLines == null)
        {
            config.SpectatorBattleChatterLines = CreateDefaultBattleChatterLines();
            configChanged = true;
        }
        if (config.SpectatorDecisiveChatterLines == null)
        {
            config.SpectatorDecisiveChatterLines = CreateDefaultDecisiveChatterLines();
            configChanged = true;
        }
        if (config.SpectatorEmoteIds == null)
        {
            config.SpectatorEmoteIds = CreateDefaultEmoteIds();
            configChanged = true;
        }

        // Do not inspect, normalize, deduplicate, or otherwise reinterpret the user's old pool.
        // The list is copied as-is so an existing config becomes the decisive-state pool exactly.
        if (config.SpectatorChatterLines != null)
        {
            config.SpectatorDecisiveChatterLines = new List<string>(config.SpectatorChatterLines);
            config.SpectatorChatterLines = null;
            configChanged = true;
        }

        if (!config.SpectatorCollectionReplaceRepairApplied)
        {
            var battleDefaults = CreateDefaultBattleChatterLines();
            var decisiveDefaults = CreateDefaultDecisiveChatterLines();
            var injectedDefaultsDetected =
                ContainsAppendedDefaultSet(config.SpectatorBattleChatterLines, battleDefaults) ||
                ContainsAppendedDefaultSet(config.SpectatorDecisiveChatterLines, decisiveDefaults);
            if (injectedDefaultsDetected)
            {
                RemoveOneDefaultSet(config.SpectatorBattleChatterLines, battleDefaults);
                RemoveOneDefaultSet(config.SpectatorDecisiveChatterLines, decisiveDefaults);
                log.Info("Repaired spectator dialogue pools affected by collection default appending.");
            }

            // The UI never permits duplicate emote IDs, so duplicates here can only come from
            // the same collection-population bug. Preserve the first occurrence and its order.
            var distinctEmoteIds = config.SpectatorEmoteIds.Distinct().ToList();
            if (distinctEmoteIds.Count != config.SpectatorEmoteIds.Count)
                config.SpectatorEmoteIds = distinctEmoteIds;

            config.SpectatorCollectionReplaceRepairApplied = true;
            configChanged = true;
        }
        if (configChanged)
            config.Save();

        // XivVoices consumes ChatMessage during Dalamud's first pass. Suppress our synthetic
        // entry in the second pass so every chat consumer sees it before the game is prevented
        // from printing it.
        Services.ChatGui.CheckMessageHandled += OnLocalBridgeCheckMessageHandled;
        chatterBridgeSubscribed = true;
    }

    private static bool ContainsAppendedDefaultSet(
        IReadOnlyCollection<string> lines,
        IReadOnlyCollection<string> defaults)
        => lines.Count > defaults.Count &&
           defaults.All(defaultLine => lines.Any(line =>
               string.Equals(line, defaultLine, StringComparison.Ordinal)));

    private static void RemoveOneDefaultSet(
        List<string> lines,
        IEnumerable<string> defaults)
    {
        foreach (var defaultLine in defaults)
        {
            var index = lines.FindIndex(line =>
                string.Equals(line, defaultLine, StringComparison.Ordinal));
            if (index >= 0)
                lines.RemoveAt(index);
        }
    }

    private void DisposeChatter()
    {
        StopAllChatter();
        if (chatterBridgeSubscribed)
        {
            Services.ChatGui.CheckMessageHandled -= OnLocalBridgeCheckMessageHandled;
            chatterBridgeSubscribed = false;
        }

        lock (pendingBridgeLock)
            pendingBridgeMessages.Clear();
    }

    private void TickChatter(float deltaTime)
    {
        chatterClock += Math.Clamp(deltaTime, 0f, 1f);
        PrunePendingBridgeMessages();

        var enabled = config.SpectatorChatterEnabled;
        if (!enabled)
        {
            if (chatterWasEnabled)
                StopAllChatter();
            chatterWasEnabled = false;
            return;
        }

        if (!chatterWasEnabled)
            chatterWasEnabled = true;

        if (spectators.Count == 0)
            return;

        var maxConcurrent = Math.Clamp(
            config.SpectatorChatterMaxConcurrent,
            1,
            MaxChatterConcurrent);
        if (ActiveChatterCount >= maxConcurrent)
            return;

        var lines = GetUsableChatterLines(allowBattlePreview: false, out var phase);
        if (phase == SpectatorChatterPhase.Waiting)
        {
            LastChatterError = string.Empty;
            return;
        }
        if (lines.Count == 0)
        {
            LastChatterError = phase == SpectatorChatterPhase.Battle
                ? "Add at least one battle dialogue line before enabling crowd chatter."
                : "Add at least one decisive dialogue line before enabling crowd chatter.";
            return;
        }

        // Every idle spectator owns an independent, frame-rate-independent probability roll.
        // Shuffle before rolling so a nearly full concurrency budget does not favor spawn order.
        var candidates = spectators
            .Where(actor =>
                !actor.DespawnRequested &&
                actor.IsDrawn &&
                actor.ChatterUntil <= chatterClock &&
                actor.NextChatterEligibleAt <= chatterClock)
            .ToList();
        for (var i = candidates.Count - 1; i > 0; i--)
        {
            var swapIndex = Random.Shared.Next(i + 1);
            (candidates[i], candidates[swapIndex]) = (candidates[swapIndex], candidates[i]);
        }

        var chancePerSecond = Math.Clamp(config.SpectatorChatterChancePerSecond, 0.1f, 100f) / 100d;
        var clampedDelta = Math.Clamp(deltaTime, 0f, 1f);
        var triggerChanceThisTick = chancePerSecond >= 1d
            ? 1d
            : 1d - Math.Pow(1d - chancePerSecond, clampedDelta);
        var availableSlots = maxConcurrent - ActiveChatterCount;
        foreach (var actor in candidates)
        {
            if (availableSlots <= 0)
                break;
            if (Random.Shared.NextDouble() >= triggerChanceThisTick)
                continue;

            var line = PickChatterLine(lines, actor.LastChatterLine);
            if (TryStartChatter(actor, line, bypassSpeakerCooldown: false, out var error))
                availableSlots--;
            else
                LastChatterError = error;
        }
    }

    public SpectatorChatterResult TrySpeakRandomNow()
    {
        if (disposed)
            return FailChatter("Spectator controller is disposed.");
        if (!clientState.IsLoggedIn || Services.ObjectTable.LocalPlayer == null)
            return FailChatter("The local player is unavailable.");

        var lines = GetUsableChatterLines(allowBattlePreview: true, out _);
        if (lines.Count == 0)
            return FailChatter("Add at least one dialogue line first.");

        var candidates = spectators
            .Where(actor => !actor.DespawnRequested && actor.IsDrawn && actor.ChatterUntil <= chatterClock)
            .ToList();
        if (candidates.Count == 0)
            return FailChatter("No idle, fully spawned spectator is available.");

        var actor = candidates[Random.Shared.Next(candidates.Count)];
        var line = PickChatterLine(lines, actor.LastChatterLine);
        if (!TryStartChatter(actor, line, bypassSpeakerCooldown: true, out var error))
            return FailChatter(error);

        return new SpectatorChatterResult(true, actor.DisplayName, line, string.Empty);
    }

    private SpectatorChatterResult FailChatter(string error)
    {
        LastChatterError = error;
        return new SpectatorChatterResult(false, string.Empty, string.Empty, error);
    }

    private List<string> GetUsableChatterLines(
        bool allowBattlePreview,
        out SpectatorChatterPhase phase)
    {
        phase = ResolveChatterPhase();
        var source = phase switch
        {
            SpectatorChatterPhase.Battle => config.SpectatorBattleChatterLines,
            SpectatorChatterPhase.Decisive => config.SpectatorDecisiveChatterLines,
            _ when allowBattlePreview => config.SpectatorBattleChatterLines,
            _ => null,
        };

        return (source ?? new List<string>())
            .Select(NormalizeChatterLine)
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxChatterLines)
            .ToList();
    }

    private SpectatorChatterPhase ResolveChatterPhase()
    {
        var hasEnemyParticipant = false;
        var hasLivingEnemy = false;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (!npc.IsSpawned || npc.BattleChara == null || npc.Address == nint.Zero)
                continue;

            hasEnemyParticipant = true;
            if (npc.IsAlive)
                hasLivingEnemy = true;
        }

        // A crowd can exist without a combat roster. In that case automatic chatter waits
        // instead of falsely treating an empty enemy roster as a decisive victory.
        if (!hasEnemyParticipant)
            return SpectatorChatterPhase.Waiting;

        return hasLivingFriendlySide() && hasLivingEnemy
            ? SpectatorChatterPhase.Battle
            : SpectatorChatterPhase.Decisive;
    }

    private static string NormalizeChatterLine(string? line)
    {
        var normalized = line?.Trim() ?? string.Empty;
        return normalized.Length <= MaxChatterLineLength
            ? normalized
            : normalized[..MaxChatterLineLength].TrimEnd();
    }

    private static string PickChatterLine(IReadOnlyList<string> lines, string previousLine)
    {
        if (lines.Count == 1)
            return lines[0];

        var previousIndex = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Equals(previousLine, StringComparison.OrdinalIgnoreCase))
            {
                previousIndex = i;
                break;
            }
        }

        if (previousIndex < 0)
            return lines[Random.Shared.Next(lines.Count)];

        var choice = Random.Shared.Next(lines.Count - 1);
        if (choice >= previousIndex)
            choice++;
        return lines[choice];
    }

    private bool TryStartChatter(
        SpectatorActor actor,
        string line,
        bool bypassSpeakerCooldown,
        out string error)
    {
        error = string.Empty;
        if (!bypassSpeakerCooldown && actor.NextChatterEligibleAt > chatterClock)
        {
            error = $"{actor.DisplayName} is still on chatter cooldown.";
            return false;
        }
        if (!TryGetOwnedObject(actor, out var battleChara, out _))
        {
            error = $"{actor.DisplayName} no longer owns its client actor slot.";
            return false;
        }

        var duration = Math.Clamp(config.SpectatorChatterBubbleDuration, 1.5f, 10f);
        try
        {
            var character = (Character*)battleChara;
            if (character->YellBalloon.State != NpcYellBalloonState.Inactive)
                character->YellBalloon.CloseBalloon();

            // This is the native client balloon owned by this synthetic Character. printToLog is
            // deliberately false; the separate local bridge below is intercepted before display.
            character->YellBalloon.OpenBalloon(
                line,
                duration,
                true,
                0f,
                false,
                false,
                true,
                DefaultBalloonParentBone);

            actor.ChatterUntil = chatterClock + duration;
            // The scheduler already avoids choosing the same actor twice when another spectator
            // is available. Keep only a short post-balloon rest here so a one-person crowd can
            // still speak at the configured interval (including after Test Once).
            actor.NextChatterEligibleAt = actor.ChatterUntil + Random.Shared.NextDouble() * 2d;
            actor.LastChatterLine = line;
            LastChatterSpeaker = actor.DisplayName;
            LastChatterText = line;
            LastChatterError = string.Empty;

            if (config.SpectatorChatterTtsBridge)
            {
                try
                {
                    EmitLocalTtsBridge(actor.DisplayName, line);
                }
                catch (Exception ex)
                {
                    log.Warning(ex, $"Local XivVoices bridge failed for spectator '{actor.DisplayName}'.");
                    LastChatterError = $"Bubble shown, but the local XivVoices bridge failed: {ex.Message}";
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to open spectator chatter balloon for '{actor.DisplayName}'.");
            error = $"Failed to open {actor.DisplayName}'s local speech bubble: {ex.Message}";
            return false;
        }
    }

    private void StopAllChatter()
    {
        foreach (var actor in spectators)
        {
            if (!TryGetOwnedObject(actor, out var battleChara, out _))
                continue;
            try
            {
                CloseChatter(actor, (Character*)battleChara);
            }
            catch (Exception ex)
            {
                log.Verbose(ex, $"Could not close spectator chatter balloon for '{actor.DisplayName}'.");
            }
        }

        chatterWasEnabled = false;
    }

    private static void CloseChatter(SpectatorActor actor, Character* character)
    {
        if (actor.ChatterUntil > 0d && character->YellBalloon.State != NpcYellBalloonState.Inactive)
            character->YellBalloon.CloseBalloon();
        actor.ChatterUntil = 0d;
    }

    private void EmitLocalTtsBridge(string speaker, string line)
    {
        var localPlayer = Services.ObjectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.HomeWorld.RowId == 0)
        {
            LastChatterError = "XivVoices bridge skipped because the local home world is unavailable.";
            return;
        }

        var pending = new PendingBridgeMessage(
            speaker,
            line,
            DateTime.UtcNow.AddSeconds(PendingBridgeLifetimeSeconds));
        lock (pendingBridgeLock)
            pendingBridgeMessages.Add(pending);

        try
        {
            // IChatGui.Print only enters Dalamud's local RaptureLogModule queue. The matching event
            // is marked handled during Dalamud's second pass, so it is visible to first-pass
            // consumers such as XivVoices but is never printed by the game and never reaches a
            // chat-send/server path.
            Services.ChatGui.Print(new XivChatEntry
            {
                Type = XivChatType.Say,
                Name = new SeString(new PlayerPayload(speaker, localPlayer.HomeWorld.RowId)),
                Message = new SeString(new TextPayload(line)),
                Silent = true,
            });
        }
        catch
        {
            lock (pendingBridgeLock)
                pendingBridgeMessages.Remove(pending);
            throw;
        }
    }

    private void OnLocalBridgeCheckMessageHandled(IHandleableChatMessage message)
    {
        if (message.LogKind != XivChatType.Say)
            return;

        string speaker;
        try
        {
            speaker = message.Sender.Payloads
                .OfType<PlayerPayload>()
                .Select(payload => payload.PlayerName)
                .FirstOrDefault() ?? string.Empty;
        }
        catch
        {
            return;
        }
        if (speaker.Length == 0)
            return;

        var sentence = message.Message.TextValue;
        var matched = false;
        lock (pendingBridgeLock)
        {
            var now = DateTime.UtcNow;
            pendingBridgeMessages.RemoveAll(item => item.ExpiresAt <= now);
            var index = pendingBridgeMessages.FindIndex(item =>
                item.Speaker.Equals(speaker, StringComparison.Ordinal) &&
                item.Sentence.Equals(sentence, StringComparison.Ordinal));
            if (index >= 0)
            {
                pendingBridgeMessages.RemoveAt(index);
                matched = true;
            }
        }

        if (matched)
            message.PreventOriginal();
    }

    private void PrunePendingBridgeMessages()
    {
        lock (pendingBridgeLock)
        {
            var now = DateTime.UtcNow;
            pendingBridgeMessages.RemoveAll(item => item.ExpiresAt <= now);
        }
    }

    public readonly record struct SpectatorChatterResult(
        bool Success,
        string Speaker,
        string Line,
        string Error);

    private sealed record PendingBridgeMessage(
        string Speaker,
        string Sentence,
        DateTime ExpiresAt);
}

public enum SpectatorChatterPhase
{
    Waiting,
    Battle,
    Decisive,
}
