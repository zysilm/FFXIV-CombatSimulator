// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace CombatSimulator.Voice;

/// <summary>One voice request raised this frame.</summary>
public readonly record struct VoiceCandidate(ulong ActorKey, VoiceEvent Event, float Distance);

public sealed class VoiceRateSettings
{
    /// <summary>Global refill rate. ~1.5/s keeps a ten-enemy brawl readable instead of a wall of noise.</summary>
    public float TokensPerSecond { get; set; } = 1.5f;
    public float Burst { get; set; } = 2f;
    public int MaxConcurrent { get; set; } = 3;
    /// <summary>How long a started voice is assumed to occupy a slot. The engine's own playback
    /// state is not trusted (auto-released SoundData slots are recycled), so concurrency is
    /// counted by time; battle shouts run roughly one to two seconds.</summary>
    public float AssumedVoiceSeconds { get; set; } = 1.8f;
    /// <summary>Minimum gap between two voices of the same actor, whatever the events.</summary>
    public float SameActorGap { get; set; } = 1.5f;
    /// <summary>Only this many enemies closest to the player may speak.</summary>
    public int ClosestSpeakers { get; set; } = 4;
}

/// <summary>
/// Decides which voice requests actually play. Pure and clocked by <see cref="Advance"/>, so the
/// whole policy is testable offline (Tests/Voice).
/// </summary>
public sealed class VoiceRateLimiter
{
    private readonly VoiceRateSettings settings;
    private readonly Dictionary<(ulong, VoiceEvent), float> eventReadyAt = new();
    private readonly Dictionary<ulong, float> actorReadyAt = new();
    private readonly List<float> activeUntil = new();
    private float tokens;

    public VoiceRateLimiter(VoiceRateSettings settings)
    {
        this.settings = settings;
        tokens = settings.Burst;
    }

    public float Now { get; private set; }
    public int ActiveVoices { get { Prune(); return activeUntil.Count; } }
    public float Tokens => tokens;

    public void Advance(float deltaTime)
    {
        if (deltaTime <= 0f || float.IsNaN(deltaTime))
            return;
        Now += deltaTime;
        tokens = MathF.Min(settings.Burst, tokens + deltaTime * settings.TokensPerSecond);
        Prune();
    }

    public void Reset()
    {
        eventReadyAt.Clear();
        actorReadyAt.Clear();
        activeUntil.Clear();
        tokens = settings.Burst;
    }

    /// <summary>Forget one actor (slot reused by a different entity, or despawned).</summary>
    public void Forget(ulong actorKey)
    {
        actorReadyAt.Remove(actorKey);
        var stale = new List<(ulong, VoiceEvent)>();
        foreach (var key in eventReadyAt.Keys)
            if (key.Item1 == actorKey)
                stale.Add(key);
        foreach (var key in stale)
            eventReadyAt.Remove(key);
    }

    public bool IsOnCooldown(ulong actorKey, VoiceEvent e) =>
        eventReadyAt.TryGetValue((actorKey, e), out var t) && Now < t;

    /// <summary>Whether a request would be admitted right now, without consuming anything.</summary>
    public bool CanAdmit(ulong actorKey, VoiceEvent e)
    {
        Prune();
        if (IsOnCooldown(actorKey, e))
            return false;
        if (actorReadyAt.TryGetValue(actorKey, out var actorT) && Now < actorT)
            return false;
        if (activeUntil.Count >= Math.Max(1, settings.MaxConcurrent))
            return false;
        return VoiceEventPolicy.IsCritical(e) || tokens >= 1f;
    }

    /// <summary>Admit and consume budget, or refuse with nothing consumed.</summary>
    public bool TryAdmit(ulong actorKey, VoiceEvent e)
    {
        if (!CanAdmit(actorKey, e))
            return false;
        Commit(actorKey, e);
        return true;
    }

    /// <summary>
    /// Mark an event as spent without playing (e.g. nothing was labeled for it). The event
    /// cooldown still starts, so an unlabeled event does not retry and starve others every frame,
    /// but no voice slot or token is used.
    /// </summary>
    /// <summary>Drop an actor's cooldown for one event (a guaranteed killing-blow line must not be
    /// refused because the same enemy gloated over a companion a few seconds earlier).</summary>
    public void ClearCooldown(ulong actorKey, VoiceEvent e) => eventReadyAt.Remove((actorKey, e));

    public void SpendCooldownOnly(ulong actorKey, VoiceEvent e) =>
        eventReadyAt[(actorKey, e)] = Now + VoiceEventPolicy.Cooldown(e);

    private void Commit(ulong actorKey, VoiceEvent e)
    {
        // A critical event admitted on an empty bucket leaves it at zero rather than in debt, so
        // one death does not silence the next two seconds of the fight.
        tokens = MathF.Max(0f, tokens - 1f);
        eventReadyAt[(actorKey, e)] = Now + VoiceEventPolicy.Cooldown(e);
        actorReadyAt[actorKey] = Now + settings.SameActorGap;
        activeUntil.Add(Now + settings.AssumedVoiceSeconds);
    }

    private void Prune() => activeUntil.RemoveAll(t => t <= Now);

    /// <summary>
    /// Order this frame's requests: only the N closest actors may speak, at most one request per
    /// actor (its highest priority), then priority high→low and nearer first on ties.
    /// </summary>
    public static List<VoiceCandidate> Arbitrate(IReadOnlyList<VoiceCandidate> candidates,
        ISet<ulong> allowedSpeakers)
    {
        var best = new Dictionary<ulong, VoiceCandidate>();
        foreach (var c in candidates)
        {
            if (!allowedSpeakers.Contains(c.ActorKey))
                continue;
            if (!best.TryGetValue(c.ActorKey, out var existing) ||
                VoiceEventPolicy.Priority(c.Event) > VoiceEventPolicy.Priority(existing.Event))
                best[c.ActorKey] = c;
        }
        var ordered = new List<VoiceCandidate>(best.Values);
        ordered.Sort((a, b) =>
        {
            var p = VoiceEventPolicy.Priority(b.Event).CompareTo(VoiceEventPolicy.Priority(a.Event));
            return p != 0 ? p : a.Distance.CompareTo(b.Distance);
        });
        return ordered;
    }

    /// <summary>Keys of the <paramref name="count"/> nearest actors.</summary>
    public static HashSet<ulong> Closest(IReadOnlyList<(ulong ActorKey, float Distance)> actors, int count)
    {
        var sorted = new List<(ulong ActorKey, float Distance)>(actors);
        sorted.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var result = new HashSet<ulong>();
        for (var i = 0; i < sorted.Count && result.Count < Math.Max(0, count); i++)
            result.Add(sorted[i].ActorKey);
        return result;
    }
}
