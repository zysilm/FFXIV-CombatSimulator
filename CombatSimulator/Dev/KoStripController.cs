// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using AdvancedRagdoll;
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
    private sealed class PreparedStrip
    {
        public nint Address;
        public uint ObjectIndex;
        public uint EntityId;
        public bool Other;
        public StripSlot Slot;
        public readonly List<GearDropOperation> Pieces = new();
        public float Age;
    }
    private readonly List<PreparedStrip> preparedStrips = new();

    // Allies and enemies ("others"): which actor an address belonged to when it was judged, and whether
    // its armour can come off (a humanoid wearing real equipment). A new actor at a recycled address is
    // judged afresh and starts with a clean once-per-actor guard.
    private readonly Dictionary<nint, (uint EntityId, bool Eligible)> others = new();

    // Each dropped piece is its own physics body and its own clone actor. The player's pieces have
    // reserved clone slots; others' pieces are capped together, the oldest retired first.
    internal const int MaxOthersDroppedPieces = 16;

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
        public readonly string? HiddenOppositeRootBone;
        public GearDropSpec(string bone, int keepSlot, bool clothing, string? hiddenOppositeRootBone = null)
        {
            Bone = bone;
            KeepSlot = keepSlot;
            Clothing = clothing;
            HiddenOppositeRootBone = hiddenOppositeRootBone;
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

    // Slots whose model can be isolated on a clone and physically dropped instead of just vanishing.
    // Hands and Feet are paired equipment models, so each produces TWO clones. HiddenOppositeRootBone
    // identifies only the other arm/leg subtree to hide; the visible side keeps its complete bind pose.
    // Clothing=true selects the clothing toggle and hides any baked-in skin material.
    private static readonly Dictionary<byte, GearDropSpec[]> Droppable = new()
    {
        [3]  = new[] { new GearDropSpec("j_kao", 0, false) }, // Head / hat
        [9]  = new[] { new GearDropSpec("j_kao", 5, false) }, // Ears
        [10] = new[] { new GearDropSpec("j_kubi", 6, false) }, // Neck
        [11] = new[] { new GearDropSpec("j_te_l", 7, false) }, // Wrists
        [12] = new[] { new GearDropSpec("j_te_r", 8, false) }, // R.Finger
        [14] = new[] { new GearDropSpec("j_te_l", 9, false) }, // L.Finger
        [4]  = new[] { new GearDropSpec("j_kosi", 1, true) }, // Body / top
        [5]  = new[]
        {
            new GearDropSpec("j_te_l", 2, true, "j_ude_a_r"), // Left glove / gauntlet; hide right arm
            new GearDropSpec("j_te_r", 2, true, "j_ude_a_l"), // Right glove / gauntlet; hide left arm
        },
        [7]  = new[] { new GearDropSpec("j_asi_b_l", 3, true) }, // Legs / pants
        [8]  = new[]
        {
            new GearDropSpec("j_asi_d_l", 4, true, "j_asi_a_r"), // Left shoe / boot; hide right leg
            new GearDropSpec("j_asi_d_r", 4, true, "j_asi_a_l"), // Right shoe / boot; hide left leg
        },
    };

    /// <summary>
    /// Strip the configured slots on the knocked-out player. Fires once per actor.
    /// </summary>
    public void StripOnKo(nint characterAddress)
    {
        if (!config.KoStripEnabled || IsOnHitDetachActive()) return;
        if (stripped.Contains(characterAddress)) return;
        DoStrip(characterAddress, other: false);
    }

    /// <summary>
    /// An ally (<paramref name="ally"/>) or an enemy fell: with "Allies" / "Enemies" ticked and the
    /// player's trigger On KO, strip it with the player's settings. Humanoids wearing real equipment
    /// only; once per actor, like the player.
    /// </summary>
    public void StripOtherOnKo(nint characterAddress, bool ally)
    {
        if (!OthersEnabled(ally) || !config.KoStripEnabled || IsOnHitDetachActive()) return;
        if (!AdmitOther(characterAddress)) return;
        if (stripped.Contains(characterAddress)) return;
        DoStrip(characterAddress, other: true);
    }

    /// <summary>Strip now, ignoring the once-per-actor guard (manual test button).</summary>
    public void StripNow(nint characterAddress) => DoStrip(characterAddress, other: false);

    public void Tick(float deltaTime) => TickPreparedStrips(deltaTime);

    public bool TryStripNextOnHit(nint characterAddress)
    {
        if (!IsOnHitDetachActive() || characterAddress == nint.Zero) return false;
        return StripNextOnHit(characterAddress, other: false);
    }

    /// <summary>
    /// An ally (<paramref name="ally"/>) or an enemy was hit by the other side: with "Allies" /
    /// "Enemies" ticked and the player's trigger On each enemy hit, the next selected piece comes off,
    /// in the player's order.
    /// </summary>
    public bool TryStripNextOnHitOther(nint characterAddress, bool ally)
    {
        if (!OthersEnabled(ally) || !IsOnHitDetachActive()) return false;
        if (!AdmitOther(characterAddress)) return false;
        return StripNextOnHit(characterAddress, other: true);
    }

    /// <summary>
    /// Forget an actor whose equipment came back (a revived ally): its pieces can come off again.
    /// Pieces already on the floor stay.
    /// </summary>
    public void Forget(nint characterAddress)
    {
        for (var i = preparedStrips.Count - 1; i >= 0; i--)
        {
            if (preparedStrips[i].Address != characterAddress) continue;
            foreach (var piece in preparedStrips[i].Pieces) piece.Cancel();
            preparedStrips.RemoveAt(i);
        }
        stripped.Remove(characterAddress);
        onHitStripped.Remove(characterAddress);
        others.Remove(characterAddress);
    }

    private bool OthersEnabled(bool ally) => ally ? config.KoStripAllies : config.KoStripEnemies;

    private bool AdmitOther(nint characterAddress)
    {
        if (characterAddress == nint.Zero) return false;
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == characterAddress) return false;
        var gameObj = (GameObject*)characterAddress;
        if (gameObj->DrawObject == null) return false;

        var entityId = gameObj->EntityId;
        if (others.TryGetValue(characterAddress, out var known))
        {
            if (known.EntityId == entityId) return known.Eligible;
            Forget(characterAddress); // a different actor now lives at this address
        }

        var eligible = dismemberment.CanDetachArmorFrom(characterAddress);
        others[characterAddress] = (entityId, eligible);
        if (!eligible)
            log.Info($"KoStrip: 0x{characterAddress:X} (idx={gameObj->ObjectIndex}) is not a humanoid wearing equipment; left as is");
        return eligible;
    }

    private bool StripNextOnHit(nint characterAddress, bool other)
    {
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

            if (!SlotRendersModel(gameObj, slot) || (other && !dismemberment.HasEquipmentInSlot(characterAddress, slot.ModelSlot)))
            {
                done.Add(apiSlot);
                continue;
            }

            var glamState = !other && ShouldPhysicsDrop(apiSlot)
                ? glamourer.GetStateBase64((int)objectIndex)
                : null;
            var result = StripOneSlot(characterAddress, character, objectIndex, slot, glamState, other);
            if (!result.Accepted) return false;
            done.Add(apiSlot);
            log.Info($"KoStrip: on-hit stripped slot {apiSlot} on 0x{characterAddress:X} (idx={objectIndex}) - " +
                     $"{result.Glamourer} via Glamourer, {result.Direct} direct, {result.Drop} physics-dropped");
            return true;
        }

        return false;
    }

    private bool IsOnHitDetachActive() => config.KoStripOnHitEnabled;

    private void DoStrip(nint characterAddress, bool other)
    {
        if (characterAddress == nint.Zero) return;
        var gameObj = (GameObject*)characterAddress;
        if (gameObj->DrawObject == null) return;
        var objectIndex = gameObj->ObjectIndex;
        var character = (Character*)characterAddress;

        // Capture the full Glamourer state BEFORE we strip anything, so a dropped piece can reproduce a
        // glam hat/accessory (which leaves the real equipment id empty). null if Glamourer absent.
        // Others' pieces are cloned from what they render (DismembermentController), never from a
        // Glamourer state.
        var anyDrop = config.KoStripPhysicsDrop || config.KoStripPhysicsDropClothing;
        var glamState = anyDrop && !other ? glamourer.GetStateBase64((int)objectIndex) : null;

        var nGlam = 0;
        var nDirect = 0;
        var nDrop = 0;
        var accepted = false;
        foreach (var slot in Slots)
        {
            if (!slot.Enabled(config)) continue;
            if (other && !dismemberment.HasEquipmentInSlot(characterAddress, slot.ModelSlot)) continue;
            var result = StripOneSlot(characterAddress, character, objectIndex, slot, glamState, other);
            nGlam += result.Glamourer;
            nDirect += result.Direct;
            nDrop += result.Drop;
            accepted |= result.Accepted;
        }

        if (accepted) stripped.Add(characterAddress);
        log.Info($"KoStrip: stripped 0x{characterAddress:X} (idx={objectIndex}) — {nGlam} via Glamourer, {nDirect} direct, {nDrop} physics-dropped");
    }

    /// <summary>Forget tracked actors so a fresh fight re-strips. No appearance change.</summary>
    public void Reset()
    {
        foreach (var preparation in preparedStrips)
            foreach (var piece in preparation.Pieces) piece.Cancel();
        preparedStrips.Clear();
        stripped.Clear();
        onHitStripped.Clear();
        others.Clear();
    }

    private readonly struct StripResult
    {
        public readonly int Glamourer;
        public readonly int Direct;
        public readonly int Drop;
        public readonly bool Accepted;

        public StripResult(int glamourer, int direct, int drop)
        {
            Glamourer = glamourer;
            Direct = direct;
            Drop = drop;
            Accepted = true;
        }
    }

    private StripResult StripOneSlot(nint characterAddress, Character* character, uint objectIndex,
        StripSlot slot, string? glamourBase64, bool other)
    {
        if (preparedStrips.Exists(p => p.Address == characterAddress && p.Slot.ApiSlot == slot.ApiSlot))
            return new StripResult(0, 0, 0);
        var nDrop = 0;
        var preparation = new PreparedStrip
        {
            Address = characterAddress, ObjectIndex = objectIndex, Slot = slot, Other = other,
            EntityId = ((GameObject*)characterAddress)->EntityId,
        };
        if (other)
        {
            var needed = 0;
            foreach (var drop in EnumerateGearDrops(slot.ApiSlot))
                if (drop.Clothing ? config.KoStripPhysicsDropClothing : config.KoStripPhysicsDrop)
                    needed++;
            if (needed > 0)
            {
                var retired = dismemberment.RetireOldestOthersGearPieces(MaxOthersDroppedPieces - needed);
                if (retired > 0)
                    log.Info($"KoStrip: others' dropped pieces at the cap ({MaxOthersDroppedPieces}); retired the oldest {retired}");
            }
        }
        foreach (var drop in EnumerateGearDrops(slot.ApiSlot))
        {
            if (!(drop.Clothing ? config.KoStripPhysicsDropClothing : config.KoStripPhysicsDrop))
                continue;
            var operation = dismemberment.SpawnGearDrop(characterAddress, drop.Bone, drop.KeepSlot, glamourBase64,
                hideSkin: drop.Clothing, hiddenOppositeRootBone: drop.HiddenOppositeRootBone);
            if (operation == null)
            {
                foreach (var piece in preparation.Pieces) piece.Cancel();
                log.Warning($"KoStrip: could not prepare slot {slot.ApiSlot}; original appearance retained");
                return default;
            }
            preparation.Pieces.Add(operation);
            nDrop++;
        }

        if (nDrop > 0)
        {
            preparedStrips.Add(preparation);
            log.Info($"KoStrip: preparing slot {slot.ApiSlot} ({nDrop} piece(s)); waiting for rendered appearance");
            return new StripResult(0, 0, 0); // Count only committed removals, never mere spawn requests.
        }

        if (glamourer.SetItem((int)objectIndex, slot.ApiSlot, 0, persist: !other))
            return new StripResult(1, 0, nDrop);

        SetEquipmentDirect(character, slot.GameSlot, 0);
        return new StripResult(0, 1, nDrop);
    }

    private void TickPreparedStrips(float deltaTime)
    {
        for (var i = preparedStrips.Count - 1; i >= 0; i--)
        {
            var preparation = preparedStrips[i];
            preparation.Age += MathF.Max(0, deltaTime);
            var player = Core.Services.ObjectTable.LocalPlayer;
            // The player's own; or, while the session is alive, the same ally/enemy still in that slot.
            var valid = player != null && (preparation.Other
                ? player.Address != preparation.Address &&
                  dismemberment.IsSameLiveActor(preparation.Address, preparation.ObjectIndex, preparation.EntityId)
                : player.Address == preparation.Address);
            var failed = !valid || preparation.Age > 8f || preparation.Pieces.Exists(p => p.IsCancelled);
            if (!failed && !preparation.Pieces.TrueForAll(p => p.IsReady)) continue;
            // Do not remove a different outfit if the player changed/reverted appearance while
            // the asynchronous clone was loading. The next request can capture the new outfit.
            if (!failed) failed = !preparation.Pieces.TrueForAll(dismemberment.IsGearDropSourceUnchanged);
            preparedStrips.RemoveAt(i);
            if (failed)
            {
                foreach (var piece in preparation.Pieces) piece.Cancel();
                stripped.Remove(preparation.Address);
                if (onHitStripped.TryGetValue(preparation.Address, out var done)) done.Remove(preparation.Slot.ApiSlot);
                log.Warning($"KoStrip: slot {preparation.Slot.ApiSlot} preparation failed/cancelled; original appearance retained; retry available");
                continue;
            }
            var character = (Character*)preparation.Address;
            if (((GameObject*)character)->ObjectIndex != preparation.ObjectIndex || ((GameObject*)character)->DrawObject == null)
            {
                foreach (var piece in preparation.Pieces) piece.Cancel();
                continue;
            }
            try
            {
                // Others are not kept stripped in Glamourer's state: their outfit comes back on a revive
                // or a reset, and an enemy of the same kind spawned later must not inherit the change.
                if (!glamourer.SetItem((int)preparation.ObjectIndex, preparation.Slot.ApiSlot, 0, persist: !preparation.Other))
                    SetEquipmentDirect(character, preparation.Slot.GameSlot, 0);
                foreach (var piece in preparation.Pieces) piece.Commit();
                log.Info($"KoStrip: committed slot {preparation.Slot.ApiSlot}; {preparation.Pieces.Count} physics-dropped");
            }
            catch (Exception ex)
            {
                foreach (var piece in preparation.Pieces) piece.Cancel();
                log.Warning(ex, $"KoStrip: slot {preparation.Slot.ApiSlot} commit failed");
            }
        }
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
        if (!Droppable.TryGetValue(apiSlot, out var drops))
            yield break;

        foreach (var drop in drops)
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
