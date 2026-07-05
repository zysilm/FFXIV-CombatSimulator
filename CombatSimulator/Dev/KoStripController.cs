using System;
using System.Collections.Generic;
using CombatSimulator.Animation;
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
    private readonly DismembermentController dismemberment;
    private readonly IPluginLog log;

    // Addresses already stripped this session — so auto-KO doesn't re-fire every frame.
    private readonly HashSet<nint> stripped = new();
    private readonly Dictionary<nint, HashSet<byte>> onHitStripped = new();
    private readonly Dictionary<nint, float> pendingKoStrip = new();

    public Func<bool> AllowOnHitDetach { private get; set; } = () => false;

    public KoStripController(Configuration config, GlamourerIpc glamourer,
        DismembermentController dismemberment, IPluginLog log)
    {
        this.config = config;
        this.glamourer = glamourer;
        this.dismemberment = dismemberment;
        this.log = log;
    }

    // Unified slot table. ApiEquipSlot bytes per Glamourer.Api:
    // Head=3, Body=4, Hands=5, Legs=7, Feet=8, Ears=9, Neck=10, Wrists=11, RFinger=12, LFinger=14.
    private readonly struct StripSlot
    {
        public readonly byte ApiSlot;
        public readonly DrawDataContainer.EquipmentSlot GameSlot;
        public readonly Func<Configuration, bool> Enabled;
        public readonly int ModelSlot;
        public StripSlot(byte apiSlot, DrawDataContainer.EquipmentSlot gameSlot,
            Func<Configuration, bool> enabled, int modelSlot)
        {
            ApiSlot = apiSlot; GameSlot = gameSlot; Enabled = enabled; ModelSlot = modelSlot;
        }
    }

    private readonly struct GearDropSpec
    {
        public readonly string Bone;
        public readonly int KeepSlot;
        public readonly bool Clothing;
        public GearDropSpec(string bone, int keepSlot, bool clothing)
        {
            Bone = bone; KeepSlot = keepSlot; Clothing = clothing;
        }
    }

    private static readonly StripSlot[] Slots =
    {
        new(3,  DrawDataContainer.EquipmentSlot.Head,    c => c.KoStripHead,    0),
        new(4,  DrawDataContainer.EquipmentSlot.Body,    c => c.KoStripBody,    1),
        new(5,  DrawDataContainer.EquipmentSlot.Hands,   c => c.KoStripHands,   2),
        new(7,  DrawDataContainer.EquipmentSlot.Legs,    c => c.KoStripLegs,    3),
        new(8,  DrawDataContainer.EquipmentSlot.Feet,    c => c.KoStripFeet,    4),
        new(9,  DrawDataContainer.EquipmentSlot.Ears,    c => c.KoStripEars,    5),
        new(10, DrawDataContainer.EquipmentSlot.Neck,    c => c.KoStripNeck,    6),
        new(11, DrawDataContainer.EquipmentSlot.Wrists,  c => c.KoStripWrists,  7),
        new(12, DrawDataContainer.EquipmentSlot.RFinger, c => c.KoStripRFinger, 8),
        new(14, DrawDataContainer.EquipmentSlot.LFinger, c => c.KoStripLFinger, 9),
    };

    private static readonly byte[] OnHitOrder = { 3, 9, 10, 11, 12, 14, 4, 5, 7, 8 };

    // Slots whose model is a separate, non-skin-fused piece, so it can physically drop instead of just
    // vanishing. Maps ApiSlot -> (attach bone the piece tumbles from, CharacterBase model-slot index to
    // keep visible). Head/hat is the clean case; accessories are approximate (single rigid drop).
    // Per droppable slot: attach bone, CharacterBase model-slot index to keep, and whether it belongs to
    // the CLOTHING toggle (true) or the HAT/ACCESSORY toggle (false).
    private static readonly Dictionary<byte, GearDropSpec> Droppable = new()
    {
        [3]  = new("j_kao",  0, false), // Head / hat
        [9]  = new("j_kao",  5, false), // Ears
        [10] = new("j_kubi", 6, false), // Neck
        [11] = new("j_te_l", 7, false), // Wrists
        [12] = new("j_te_r", 8, false), // R.Finger
        [14] = new("j_te_l", 9, false), // L.Finger
        [4]  = new("j_kosi", 1, true),  // Body / top — clothing (WIP shell drop incl. torso skin)
        [7]  = new("j_asi_b_l", 3, true), // Legs / pants
    };

    /// <summary>
    /// Strip the configured slots on the knocked-out player. Player-only by design — NPC draw
    /// objects vary too much (non-humanoid, partial gear) to strip reliably. Fires once per actor.
    /// </summary>
    public void StripOnKo(nint characterAddress)
    {
        if (!config.KoStripEnabled || IsOnHitDetachActive()) return;
        if (stripped.Contains(characterAddress)) return;
        if (pendingKoStrip.ContainsKey(characterAddress)) return;

        var delay = config.KoStripSyncWithRagdoll ? MathF.Max(0f, config.RagdollActivationDelay) : 0f;
        if (delay > 0.001f)
        {
            pendingKoStrip[characterAddress] = delay;
            log.Info($"KoStrip: scheduled ragdoll-synced strip for 0x{characterAddress:X} after {delay:F2}s");
            return;
        }

        DoStrip(characterAddress);
    }

    /// <summary>Strip now, ignoring the once-per-actor guard (manual test button).</summary>
    public void StripNow(nint characterAddress)
    {
        pendingKoStrip.Remove(characterAddress);
        DoStrip(characterAddress);
    }

    public void Tick(float deltaTime)
    {
        if (pendingKoStrip.Count == 0) return;
        if (!config.KoStripEnabled || IsOnHitDetachActive())
        {
            pendingKoStrip.Clear();
            return;
        }

        var dt = MathF.Max(0f, deltaTime);
        var keys = new List<nint>(pendingKoStrip.Keys);
        foreach (var address in keys)
        {
            if (!pendingKoStrip.TryGetValue(address, out var remaining))
                continue;

            remaining -= dt;
            if (remaining > 0f)
            {
                pendingKoStrip[address] = remaining;
                continue;
            }

            pendingKoStrip.Remove(address);
            if (!stripped.Contains(address))
                DoStrip(address);
        }
    }

    public bool TryStripNextOnHit(nint characterAddress)
    {
        if (!IsOnHitDetachActive() || characterAddress == nint.Zero) return false;
        var gameObj = (GameObject*)characterAddress;
        if (gameObj->DrawObject == null) return false;
        var objectIndex = gameObj->ObjectIndex;
        var character = (Character*)characterAddress;

        if (!onHitStripped.TryGetValue(characterAddress, out var done))
            onHitStripped[characterAddress] = done = new HashSet<byte>();

        foreach (var apiSlot in OnHitOrder)
        {
            if (!TryGetSlot(apiSlot, out var slot) || !slot.Enabled(config) || done.Contains(apiSlot))
                continue;

            if (!SlotRendersModel(gameObj, slot))
            {
                done.Add(apiSlot);
                continue;
            }

            var glamState = ShouldPhysicsDrop(apiSlot)
                ? glamourer.GetStateBase64((int)objectIndex)
                : null;
            var result = StripOneSlot(characterAddress, character, objectIndex, slot, glamState);
            done.Add(apiSlot);
            log.Info($"KoStrip: on-hit stripped slot {apiSlot} on 0x{characterAddress:X} (idx={objectIndex}) - " +
                     $"{result.Glamourer} via Glamourer, {result.Direct} direct, {result.Drop} physics-dropped");
            return true;
        }

        return false;
    }

    private bool IsOnHitDetachActive() => config.KoStripOnHitEnabled && AllowOnHitDetach();

    private void DoStrip(nint characterAddress)
    {
        if (characterAddress == nint.Zero) return;
        var gameObj = (GameObject*)characterAddress;
        if (gameObj->DrawObject == null) return;
        var objectIndex = gameObj->ObjectIndex;
        var character = (Character*)characterAddress;

        // Capture the full Glamourer state BEFORE we strip anything, so a dropped piece can reproduce a
        // glam hat/accessory (which leaves the real equipment id empty). null if Glamourer absent.
        var anyDrop = config.KoStripPhysicsDrop || config.KoStripPhysicsDropClothing;
        var glamState = anyDrop ? glamourer.GetStateBase64((int)objectIndex) : null;

        var nGlam = 0;
        var nDirect = 0;
        var nDrop = 0;
        foreach (var slot in Slots)
        {
            if (!slot.Enabled(config)) continue;
            var result = StripOneSlot(characterAddress, character, objectIndex, slot, glamState);
            nGlam += result.Glamourer;
            nDirect += result.Direct;
            nDrop += result.Drop;
        }

        stripped.Add(characterAddress);
        log.Info($"KoStrip: stripped 0x{characterAddress:X} (idx={objectIndex}) — {nGlam} via Glamourer, {nDirect} direct, {nDrop} physics-dropped");
    }

    /// <summary>Forget tracked actors so a fresh fight re-strips. No appearance change.</summary>
    public void Reset()
    {
        stripped.Clear();
        pendingKoStrip.Clear();
        onHitStripped.Clear();
    }

    private readonly struct StripResult
    {
        public readonly int Glamourer;
        public readonly int Direct;
        public readonly int Drop;

        public StripResult(int glamourer, int direct, int drop)
        {
            Glamourer = glamourer;
            Direct = direct;
            Drop = drop;
        }
    }

    private StripResult StripOneSlot(nint characterAddress, Character* character, uint objectIndex,
        StripSlot slot, string? glamourBase64)
    {
        var nDrop = 0;
        foreach (var drop in EnumerateGearDrops(slot.ApiSlot))
        {
            if (!(drop.Clothing ? config.KoStripPhysicsDropClothing : config.KoStripPhysicsDrop))
                continue;
            dismemberment.SpawnGearDrop(characterAddress, drop.Bone, drop.KeepSlot, glamourBase64,
                hideSkin: drop.Clothing);
            nDrop++;
        }

        if (glamourer.SetItem((int)objectIndex, slot.ApiSlot, 0, persist: true))
            return new StripResult(1, 0, nDrop);

        SetEquipmentDirect(character, slot.GameSlot, 0);
        return new StripResult(0, 1, nDrop);
    }

    private static bool TryGetSlot(byte apiSlot, out StripSlot slot)
    {
        foreach (var s in Slots)
        {
            if (s.ApiSlot != apiSlot) continue;
            slot = s;
            return true;
        }

        slot = default;
        return false;
    }

    private IEnumerable<GearDropSpec> EnumerateGearDrops(byte apiSlot)
    {
        if (Droppable.TryGetValue(apiSlot, out var drop))
            yield return drop;
    }

    private bool ShouldPhysicsDrop(byte apiSlot)
    {
        foreach (var drop in EnumerateGearDrops(apiSlot))
            if (drop.Clothing ? config.KoStripPhysicsDropClothing : config.KoStripPhysicsDrop)
                return true;
        return false;
    }

    private static bool SlotRendersModel(GameObject* gameObj, StripSlot slot)
    {
        if (gameObj->DrawObject == null) return false;
        var cb = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)gameObj->DrawObject;
        return cb->Models != null
               && slot.ModelSlot >= 0
               && slot.ModelSlot < cb->SlotCount
               && cb->Models[slot.ModelSlot] != null;
    }

    private static void SetEquipmentDirect(Character* character, DrawDataContainer.EquipmentSlot slot, ulong value)
    {
        var model = new EquipmentModelId { Value = value };
        character->DrawData.Equipment(slot).Value = value;
        character->DrawData.LoadEquipment(slot, &model, true);
    }

    public void Dispose() => Reset();
}
