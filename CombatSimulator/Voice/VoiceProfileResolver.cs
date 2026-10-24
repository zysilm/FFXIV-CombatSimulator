// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace CombatSimulator.Voice;

/// <summary>Who is speaking, as far as voice selection cares.</summary>
public readonly record struct VoiceSpeaker(nint Address, uint EntityId, uint BNpcBaseId, bool IsClone);

/// <summary>
/// Picks the voice family for a live actor and caches it per address+entity id (a reused native
/// slot with a new entity gets re-resolved). Human models use their Customize; everything else uses
/// its ModelChara sound pack.
/// </summary>
public sealed unsafe class VoiceProfileResolver
{
    private readonly IDataManager dataManager;
    private readonly Dictionary<(nint, uint), VoiceSource> cache = new();

    public VoiceProfileResolver(IDataManager dataManager) => this.dataManager = dataManager;

    /// <summary>Client language as the two-letter code the voice files use.</summary>
    public string ClientLanguage => dataManager.Language switch
    {
        Dalamud.Game.ClientLanguage.Japanese => "ja",
        Dalamud.Game.ClientLanguage.German => "de",
        Dalamud.Game.ClientLanguage.French => "fr",
        _ => "en",
    };

    public void Clear()
    {
        cache.Clear();
        bodyCache.Clear();
    }

    public VoiceSource Resolve(in VoiceSpeaker speaker)
    {
        if (speaker.Address == 0)
            return VoiceSource.None;
        var key = (speaker.Address, speaker.EntityId);
        if (cache.TryGetValue(key, out var cached))
            return cached;
        var source = ResolveUncached(speaker);
        cache[key] = source;
        return source;
    }

    public void Forget(nint address, uint entityId)
    {
        cache.Remove((address, entityId));
        bodyCache.Remove((address, entityId));
    }

    private readonly Dictionary<(nint, uint), (VoiceLineVoice Voice, VoiceLineAge Age)> bodyCache = new();

    /// <summary>
    /// Which voiced lines suit this actor: human models by their Customize sex byte, everything
    /// else non-human (monsters and demihumans, which speak with the processed boss voices). Same
    /// human test as <see cref="Resolve"/>; anything unreadable counts as non-human, so it never gets
    /// a human voice of the wrong gender.
    /// </summary>
    public VoiceLineVoice ResolveBody(nint address, uint entityId) => ResolveBodyAndAge(address, entityId).Voice;

    /// <summary><see cref="ResolveBody"/> plus the body age from the Customize BodyType byte
    /// (<see cref="VoiceLineAge.Any"/> for non-humans and unknown body types).</summary>
    public (VoiceLineVoice Voice, VoiceLineAge Age) ResolveBodyAndAge(nint address, uint entityId)
    {
        if (address == 0)
            return (VoiceLineVoice.NonHuman, VoiceLineAge.Any);
        var key = (address, entityId);
        if (bodyCache.TryGetValue(key, out var cached))
            return cached;
        var body = VoiceLineVoice.NonHuman;
        var age = VoiceLineAge.Any;
        try
        {
            var character = (Character*)address;
            var modelCharaId = character->ModelContainer.ModelCharaId;
            var model = modelCharaId < 0 ? null : dataManager.GetExcelSheet<ModelChara>()?.GetRowOrDefault((uint)modelCharaId);
            if (modelCharaId == 0 || model?.Type == 1)
            {
                // Customize byte 1 is the body sex (0 male, 1 female), as the shout mapping reads it;
                // byte 2 is BodyType (1 adult, 3 elderly, 4 child).
                var customize = (byte*)&character->DrawData.CustomizeData;
                body = customize[1] switch
                {
                    0 => VoiceLineVoice.Male,
                    1 => VoiceLineVoice.Female,
                    _ => VoiceLineVoice.NonHuman,
                };
                if (body != VoiceLineVoice.NonHuman)
                    age = VoiceLineEntry.AgeFromBodyType(customize[2]);
            }
        }
        catch (Exception)
        {
            body = VoiceLineVoice.NonHuman;
            age = VoiceLineAge.Any;
        }
        bodyCache[key] = (body, age);
        return (body, age);
    }

    private VoiceSource ResolveUncached(in VoiceSpeaker speaker)
    {
        try
        {
            var character = (Character*)speaker.Address;
            var modelCharaId = character->ModelContainer.ModelCharaId;
            var model = modelCharaId < 0 ? null : dataManager.GetExcelSheet<ModelChara>()?.GetRowOrDefault((uint)modelCharaId);
            // Type 1 = human skeleton driven by Customize (ModelCharaId 0 is the plain human row).
            if (modelCharaId == 0 || model?.Type == 1)
                return ResolveHuman(character, speaker);
            return VoiceSelection.MonsterFromSePack(model?.SEPack ?? 0);
        }
        catch (Exception)
        {
            return VoiceSource.None;
        }
    }

    private VoiceSource ResolveHuman(Character* character, in VoiceSpeaker speaker)
    {
        // Clones and companions are copies of real players: a player shout matching their look is
        // the obvious voice. Real humanoid field enemies are NPCs and keep a consistent NPC voice
        // per BNpcBase.
        if (!speaker.IsClone)
        {
            var npc = VoiceSelection.NpcBattleFromId(speaker.BNpcBaseId);
            if (!npc.IsNone)
                return npc;
        }
        var customize = new ReadOnlySpan<byte>(&character->DrawData.CustomizeData, 26);
        // Seed from the whole look so the same appearance always gets the same variant.
        var seed = VoiceSelection.StableHash(customize);
        return VoiceSelection.PcShoutFromCustomize(customize[0], customize[4], customize[1], seed);
    }

    /// <summary>Entry count of a source family, for validating labels (0 = unknown, accept any).</summary>
    public static int EntryCount(VoiceSource source) => source.Kind switch
    {
        VoiceSourceKind.PcShout => VoiceSelection.PcShoutEntryCount,
        VoiceSourceKind.NpcBattle => VoiceSelection.NpcBattleEntryCount,
        _ => 0,
    };
}
