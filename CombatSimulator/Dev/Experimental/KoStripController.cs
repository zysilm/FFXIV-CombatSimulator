using System;
using System.Collections.Generic;
using CombatSimulator.Integration;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Dev;

/// <summary>
/// Hidden dev mode: "Strip KO". On knockout, visually unequips the selected gear /
/// accessory slots — a dramatic fighting-game style strip. Tier 1: purely visual, no
/// physics drop. No restore — the strip is left in place (Glamourer keeps it, or it
/// clears on the next natural redraw / zone change).
///
/// Per slot it prefers Glamourer's <c>SetItem(slot, itemId:0)</c> (Glamourer resolves its
/// per-slot "Nothing" item) applied WITHOUT the <c>Once</c> flag, so Glamourer retains it
/// and re-applies on redraw — a raw draw-object write alone is reverted by Glamourer's
/// equipment-update hook. If Glamourer isn't present (SetItem fails), it falls back to a
/// direct smallclothes write on the draw object (same idiom as the companion defeat
/// appearance). Either way it only touches visual/draw state, so it generates no packets.
/// </summary>
public unsafe class KoStripController : IDisposable
{
    private readonly Configuration config;
    private readonly GlamourerIpc glamourer;
    private readonly IPluginLog log;

    // Addresses already stripped this session — so auto-KO doesn't re-fire every frame.
    private readonly HashSet<nint> stripped = new();

    public KoStripController(Configuration config, GlamourerIpc glamourer, IPluginLog log)
    {
        this.config = config;
        this.glamourer = glamourer;
        this.log = log;
    }

    // Unified slot table. ApiEquipSlot bytes per Glamourer.Api:
    // Head=3, Body=4, Hands=5, Legs=7, Feet=8, Ears=9, Neck=10, Wrists=11, RFinger=12, LFinger=14.
    private readonly struct StripSlot
    {
        public readonly byte ApiSlot;
        public readonly DrawDataContainer.EquipmentSlot GameSlot;
        public readonly Func<Configuration, bool> Enabled;
        public StripSlot(byte apiSlot, DrawDataContainer.EquipmentSlot gameSlot, Func<Configuration, bool> enabled)
        {
            ApiSlot = apiSlot; GameSlot = gameSlot; Enabled = enabled;
        }
    }

    private static readonly StripSlot[] Slots =
    {
        new(3,  DrawDataContainer.EquipmentSlot.Head,    c => c.KoStripHead),
        new(4,  DrawDataContainer.EquipmentSlot.Body,    c => c.KoStripBody),
        new(5,  DrawDataContainer.EquipmentSlot.Hands,   c => c.KoStripHands),
        new(7,  DrawDataContainer.EquipmentSlot.Legs,    c => c.KoStripLegs),
        new(8,  DrawDataContainer.EquipmentSlot.Feet,    c => c.KoStripFeet),
        new(9,  DrawDataContainer.EquipmentSlot.Ears,    c => c.KoStripEars),
        new(10, DrawDataContainer.EquipmentSlot.Neck,    c => c.KoStripNeck),
        new(11, DrawDataContainer.EquipmentSlot.Wrists,  c => c.KoStripWrists),
        new(12, DrawDataContainer.EquipmentSlot.RFinger, c => c.KoStripRFinger),
        new(14, DrawDataContainer.EquipmentSlot.LFinger, c => c.KoStripLFinger),
    };

    /// <summary>
    /// Strip the configured slots on the knocked-out player. Player-only by design — NPC draw
    /// objects vary too much (non-humanoid, partial gear) to strip reliably. Fires once per actor.
    /// </summary>
    public void StripOnKo(nint characterAddress)
    {
        if (!config.KoStripEnabled) return;
        if (stripped.Contains(characterAddress)) return;
        DoStrip(characterAddress);
    }

    /// <summary>Strip now, ignoring the once-per-actor guard (manual test button).</summary>
    public void StripNow(nint characterAddress) => DoStrip(characterAddress);

    private void DoStrip(nint characterAddress)
    {
        if (characterAddress == nint.Zero) return;
        var gameObj = (GameObject*)characterAddress;
        if (gameObj->DrawObject == null) return;
        var objectIndex = gameObj->ObjectIndex;
        var character = (Character*)characterAddress;

        var nGlam = 0;
        var nDirect = 0;
        foreach (var slot in Slots)
        {
            if (!slot.Enabled(config)) continue;

            if (glamourer.SetItem(objectIndex, slot.ApiSlot, 0, persist: true))
            {
                nGlam++;
            }
            else
            {
                // Fallback only effective when Glamourer isn't managing the actor.
                SetEquipmentDirect(character, slot.GameSlot, 0);
                nDirect++;
            }
        }

        stripped.Add(characterAddress);
        log.Info($"KoStrip: stripped 0x{characterAddress:X} (idx={objectIndex}) — {nGlam} via Glamourer, {nDirect} direct");
    }

    /// <summary>Forget tracked actors so a fresh fight re-strips. No appearance change.</summary>
    public void Reset() => stripped.Clear();

    private static void SetEquipmentDirect(Character* character, DrawDataContainer.EquipmentSlot slot, ulong value)
    {
        var model = new EquipmentModelId { Value = value };
        character->DrawData.Equipment(slot).Value = value;
        character->DrawData.LoadEquipment(slot, &model, true);
    }

    public void Dispose() => Reset();
}
