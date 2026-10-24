// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using CombatSimulator.Core;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Spectators;

/// <summary>
/// One-shot crowd reactions. The reaction is blended over each spectator's assigned loop (its
/// base override is never touched), so the crowd returns to what it was doing on its own.
/// </summary>
public sealed unsafe partial class SpectatorController
{
    private readonly List<PendingReaction> pendingReactions = new();
    private readonly Dictionary<uint, ushort> reactionTimelines = new();

    private sealed class PendingReaction
    {
        public uint EntityId;
        public float Remaining;
        public ushort Timeline;
    }

    /// <summary>Queue a reaction for the drawn crowd; ignored unless SpectatorReactionEmotes.</summary>
    public void React(SpectatorReaction reaction)
    {
        if (!config.SpectatorReactionEmotes || disposed || spectators.Count == 0) return;
        var pool = SpectatorReactionPlan.Pool(reaction);
        foreach (var actor in spectators)
        {
            if (actor.DespawnRequested || !actor.IsDrawn) continue;
            if (Random.Shared.NextDouble() >= SpectatorReactionPlan.ResponseChance) continue;
            // A spectator already reacting keeps its current reaction rather than restarting.
            if (pendingReactions.Exists(p => p.EntityId == actor.EntityId)) continue;
            var pick = WeightedPick.Pick(pool, e => ReactionTimeline(e.EmoteId) != 0 ? e.Weight : 0f, Random.Shared.NextDouble());
            if (pick < 0) continue;
            pendingReactions.Add(new PendingReaction
            {
                EntityId = actor.EntityId,
                Remaining = Random.Shared.NextSingle() * SpectatorReactionPlan.MaxResponseDelay,
                Timeline = ReactionTimeline(pool[pick].EmoteId),
            });
        }
    }

    private void TickReactions(float deltaTime)
    {
        for (var i = pendingReactions.Count - 1; i >= 0; i--)
        {
            var pending = pendingReactions[i];
            pending.Remaining -= Math.Clamp(deltaTime, 0f, 0.25f);
            if (pending.Remaining > 0f) continue;
            pendingReactions.RemoveAt(i);
            var actor = spectators.Find(a => a.EntityId == pending.EntityId);
            // Re-checked at fire time: the crowd may have been rerolled or despawned meanwhile.
            if (actor == null || actor.DespawnRequested || !actor.IsDrawn ||
                !TryGetOwnedObject(actor, out var battleChara, out _))
                continue;
            try
            {
                emotePlayer.PlayOneShot((Character*)battleChara, pending.Timeline);
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"Spectator '{actor.DisplayName}' reaction failed.");
            }
        }
    }

    private void ClearReactions() => pendingReactions.Clear();

    private ushort ReactionTimeline(uint emoteId)
    {
        if (reactionTimelines.TryGetValue(emoteId, out var cached)) return cached;
        cached = 0;
        try
        {
            var row = Services.DataManager.GetExcelSheet<Emote>()?.GetRowOrDefault(emoteId);
            if (row != null)
            {
                // Single-part emotes keep their motion in the loop slot; two-part ones in the intro.
                var intro = (ushort)row.Value.ActionTimeline[1].RowId;
                cached = intro != 0 ? intro : (ushort)row.Value.ActionTimeline[0].RowId;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Spectator reaction emote {emoteId} could not be resolved.");
        }
        reactionTimelines[emoteId] = cached;
        return cached;
    }
}
