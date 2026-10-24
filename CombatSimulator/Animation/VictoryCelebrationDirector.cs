// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using CombatSimulator.Core;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Animation;

/// <summary>
/// Post-fight presentation for actors who are still standing. Humanoids cycle through a weighted
/// pool of player emotes; creatures that cannot play those replay a flourish from their own
/// model family. Everything is local timeline playback — nothing is sent anywhere.
///
/// Deliberately separate from the legacy cinematic victory director: it never moves, snaps or
/// re-registers actors, it only animates them where they stand, and it can be told to let go of
/// any single actor (a story taking it over) without disturbing the rest.
/// </summary>
public sealed unsafe class VictoryCelebrationDirector
{
    private readonly AnimationController animation;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly List<Performer> performers = new();
    private readonly Dictionary<uint, (ushort Loop, ushort Intro)> emoteCache = new();
    private CelebrationMood? outcome;

    /// <summary>Whether the outcome this celebration belongs to still stands (player revived, new
    /// enemies joined, simulation stopped → false). Checked every tick; null means always.</summary>
    public Func<CelebrationMood, bool>? OutcomeHolds { get; set; }

    /// <summary>Living companions for a party victory, as (address, entity id).</summary>
    public Func<IEnumerable<(nint Address, uint EntityId)>>? Companions { get; set; }

    /// <summary>Stagger ceiling in seconds; read when a celebration begins.</summary>
    public Func<float> MaxStagger { get; set; } = () => 1.5f;

    public bool IsActive => performers.Count > 0;
    public int PerformerCount => performers.Count;

    private sealed class Performer
    {
        public nint Address;
        public uint EntityId;
        public CelebrationMood Mood;
        public bool Humanoid;
        public ushort FlourishTimeline;
        public ushort Loop;
        public int LastPick = -1;
        public ulong TargetObjectId;
        public required CelebrationCue Cue;
    }

    public VictoryCelebrationDirector(AnimationController animation, IDataManager dataManager, IPluginLog log)
    {
        this.animation = animation;
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>Start (or extend) a celebration. Actors already performing are left alone, so a
    /// story can add bystanders on top of a running victory without restarting anyone.</summary>
    public void Begin(CelebrationMood mood, IEnumerable<(nint Address, uint EntityId)> actors, ulong targetObjectId)
    {
        if (mood != CelebrationMood.Bystander) outcome = mood;
        var stagger = VictoryCelebrationPlan.BoundedStagger(MaxStagger());
        foreach (var (address, _) in actors)
        {
            // Track the object table's own id, not the simulator's: a possessed map enemy can carry
            // a different simulated id, and the live id is what detects a recycled slot later.
            if (address == nint.Zero || performers.Exists(p => p.Address == address) || !TryGetLiveEntity(address, out var entityId))
                continue;
            var character = (Character*)address;
            var humanoid = IsEmoteCapable(character);
            ushort flourish = 0;
            // A creature's only safe animation is an attack-shaped flourish; that is not idling.
            if (!humanoid && mood == CelebrationMood.Bystander) continue;
            if (!humanoid)
            {
                flourish = animation.SelectModelFlourishTimeline((uint)Math.Max(0, character->ModelContainer.ModelCharaId));
                // A creature with no family timeline has nothing safe to play; leave it be rather
                // than feeding it a humanoid timeline its skeleton cannot run.
                if (flourish == 0) continue;
            }
            var (min, max) = VictoryCelebrationPlan.Interval(mood, humanoid);
            performers.Add(new Performer
            {
                Address = address,
                EntityId = entityId,
                Mood = mood,
                Humanoid = humanoid,
                FlourishTimeline = flourish,
                TargetObjectId = targetObjectId,
                Cue = new CelebrationCue(Random.Shared.NextSingle() * stagger, min, max),
            });
        }
        if (performers.Count > 0)
            log.Info($"VictoryCelebration: {mood} with {performers.Count} performer(s).");
    }

    public void Tick(float deltaTime)
    {
        if (performers.Count == 0) return;
        if (Services.ObjectTable.LocalPlayer == null)
        {
            // Logged out / zoning: the actors are already gone; drop bookkeeping only.
            performers.Clear();
            outcome = null;
            return;
        }
        if (outcome is { } current && OutcomeHolds?.Invoke(current) == false)
        {
            Stop();
            return;
        }
        for (var i = performers.Count - 1; i >= 0; i--)
        {
            var performer = performers[i];
            if (!IsLive(performer.Address, performer.EntityId))
            {
                performers.RemoveAt(i);
                continue;
            }
            if (performer.Cue.Advance(deltaTime, Random.Shared.NextDouble()))
                Play(performer);
        }
    }

    /// <summary>
    /// Re-apply held loops after NPC AI ran this frame. An enemy that was mid-approach when the
    /// fight ended keeps a stale "moving" visual state, and the AI's per-frame movement cleanup
    /// zeroes the base override every frame — without this the loop never survives to render.
    /// </summary>
    public void Reassert()
    {
        foreach (var performer in performers)
        {
            if (performer.Loop == 0 || performer.Cue.Plays == 0 || !IsLive(performer.Address, performer.EntityId))
                continue;
            var character = (Character*)performer.Address;
            if (character->Timeline.BaseOverride != performer.Loop)
                character->Timeline.BaseOverride = performer.Loop;
        }
    }

    /// <summary>Hand one actor back (a story is about to direct it).</summary>
    public void Release(nint address)
    {
        var index = performers.FindIndex(p => p.Address == address);
        if (index < 0) return;
        ResetPerformer(performers[index]);
        performers.RemoveAt(index);
    }

    /// <summary>Release only actors added for one mood (a story's bystanders), keeping the rest.</summary>
    public void Release(CelebrationMood mood)
    {
        for (var i = performers.Count - 1; i >= 0; i--)
        {
            if (performers[i].Mood != mood) continue;
            ResetPerformer(performers[i]);
            performers.RemoveAt(i);
        }
    }

    public bool IsPerforming(nint address) => performers.Exists(p => p.Address == address);

    public void Stop()
    {
        foreach (var performer in performers) ResetPerformer(performer);
        performers.Clear();
        outcome = null;
    }

    private void Play(Performer performer)
    {
        var character = (Character*)performer.Address;
        if (!performer.Humanoid)
        {
            animation.EmotePlayer.PlayOneShot(character, performer.FlourishTimeline, performer.TargetObjectId);
            return;
        }
        var pool = VictoryCelebrationPlan.PoolFor(performer.Mood);
        // Unresolvable rows weigh nothing, so they can never be picked.
        var pick = WeightedPick.Pick(pool, e => Resolve(e.EmoteId).Loop != 0 || Resolve(e.EmoteId).Intro != 0 ? e.Weight : 0f,
            Random.Shared.NextDouble(), performer.LastPick);
        if (pick < 0) return;
        performer.LastPick = pick;
        var (loop, intro) = Resolve(pool[pick].EmoteId);
        performer.Loop = loop;
        // Same intro+loop pattern the spectator crowd uses; the loop row repeats as a base override.
        animation.EmotePlayer.PlayLoopedEmote(character, loop, intro, performer.TargetObjectId);
    }

    private void ResetPerformer(Performer performer)
    {
        if (performer.Cue.Plays == 0 || !IsLive(performer.Address, performer.EntityId)) return;
        var character = (Character*)performer.Address;
        if (performer.Humanoid) animation.EmotePlayer.ResetEmote(character);
    }

    private (ushort Loop, ushort Intro) Resolve(uint emoteId)
    {
        if (emoteCache.TryGetValue(emoteId, out var cached)) return cached;
        cached = default;
        try
        {
            var row = dataManager.GetExcelSheet<Emote>()?.GetRowOrDefault(emoteId);
            if (row != null)
                cached = ((ushort)row.Value.ActionTimeline[0].RowId, (ushort)row.Value.ActionTimeline[1].RowId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"VictoryCelebration: emote {emoteId} could not be resolved.");
        }
        emoteCache[emoteId] = cached;
        return cached;
    }

    /// <summary>Player emotes need the shared human skeleton: player-race models (ModelChara 0,
    /// as on cloned/virtual humans) or ModelChara rows of the human type. DemiHumans (2) and
    /// monsters (3) have their own skeletons and use their model family instead.</summary>
    private bool IsEmoteCapable(Character* character)
    {
        var modelCharaId = character->ModelContainer.ModelCharaId;
        if (modelCharaId <= 0) return true;
        try
        {
            var model = dataManager.GetExcelSheet<ModelChara>()?.GetRowOrDefault((uint)modelCharaId);
            return model?.Type == 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLive(nint address, uint entityId)
        => TryGetLiveEntity(address, out var live) && live == entityId;

    private static bool TryGetLiveEntity(nint address, out uint entityId)
    {
        entityId = 0;
        if (address == nint.Zero || Services.ObjectTable.LocalPlayer == null) return false;
        foreach (var obj in Services.ObjectTable)
        {
            if (obj.Address != address) continue;
            entityId = obj.EntityId;
            return true;
        }
        return false;
    }
}
