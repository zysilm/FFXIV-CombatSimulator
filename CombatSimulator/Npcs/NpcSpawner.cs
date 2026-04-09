using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using CombatSimulator.Simulation;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;

namespace CombatSimulator.Npcs;

/// <summary>
/// Spawns client-side BattleChara objects using the same approach as RaidsRewritten:
/// Frame 1: CreateBattleCharacter → configure properties → set ModelCharaId → RenderFlags=0
/// Frame 2+: Poll IsReadyToDraw() → EnableDraw()
/// </summary>
public unsafe class NpcSpawner : IDisposable
{
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private readonly List<SimulatedNpc> spawnedNpcs = new();
    private readonly List<PendingSpawn> pendingSpawns = new();
    private readonly ConcurrentQueue<NpcSpawnRequest> spawnQueue = new();
    private readonly HashSet<int> allocatedIndices = new();
    private uint nextEntityId = 0xF0000001;

    private const int MaxPendingFrames = 120;

    public IReadOnlyList<SimulatedNpc> SpawnedNpcs => spawnedNpcs;
    public int PendingCount => pendingSpawns.Count + spawnQueue.Count;
    public int TotalCount => spawnedNpcs.Count + pendingSpawns.Count + spawnQueue.Count;
    public int MaxNpcs => 10;

    public Action<SimulatedNpc>? OnNpcSpawnComplete { get; set; }
    public Action<string>? OnSpawnError { get; set; }

    /// <summary>
    /// When true, spawn mode is active: all player actions are routed to spawned NPCs
    /// and the game's target system is bypassed for combat.
    /// </summary>
    public bool SpawnModeActive { get; set; }

    /// <summary>
    /// Returns the last alive spawned NPC, used as the automatic attack target in spawn mode.
    /// </summary>
    public SimulatedNpc? GetLastAliveSpawnedNpc()
    {
        for (int i = spawnedNpcs.Count - 1; i >= 0; i--)
        {
            if (spawnedNpcs[i].IsSpawned && spawnedNpcs[i].IsAlive)
                return spawnedNpcs[i];
        }
        return null;
    }

    public NpcSpawner(IObjectTable objectTable, IDataManager dataManager, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>
    /// Queue a spawn request. Safe to call from any thread (ImGui Draw, command handler, etc.).
    /// </summary>
    public void QueueSpawn(NpcSpawnRequest request)
    {
        if (TotalCount >= MaxNpcs)
        {
            OnSpawnError?.Invoke("Maximum NPC limit reached.");
            return;
        }

        spawnQueue.Enqueue(request);
    }

    /// <summary>
    /// Called each Framework.Update. Processes queued spawns and pending draw enables.
    /// </summary>
    public void Tick()
    {
        while (spawnQueue.TryDequeue(out var request))
        {
            ProcessSpawnRequest(request);
        }

        TickPendingSpawns();
    }

    private void ProcessSpawnRequest(NpcSpawnRequest request)
    {
        try
        {
            var npcName = GetNpcName(request);
            var npcLevel = request.Level;
            var spawnPos = request.Position ?? CalculateSpawnPosition();
            var spawnRot = request.Rotation ?? CalculateSpawnRotation(spawnPos);

            var clientObjMgr = ClientObjectManager.Instance();
            if (clientObjMgr == null)
            {
                log.Error("ClientObjectManager is null.");
                OnSpawnError?.Invoke("ClientObjectManager is null. Are you logged in?");
                return;
            }

            // Step 1: Create BattleCharacter with explicit index hint.
            // Default (0xFFFFFFFF) rescans from 0 and may reuse occupied slots.
            // Pass an incrementing hint so each spawn gets a unique pool slot.
            uint hint = 0;
            while (allocatedIndices.Contains((int)hint) && hint < 200)
                hint++;
            log.Info($"Calling CreateBattleCharacter(hint={hint})...");
            var createResult = clientObjMgr->CreateBattleCharacter(hint);
            log.Info($"CreateBattleCharacter returned: {createResult} (0x{createResult:X})");

            if (createResult == 0xFFFFFFFF)
            {
                log.Error("CreateBattleCharacter failed.");
                OnSpawnError?.Invoke("CreateBattleCharacter failed - no available slot.");
                return;
            }

            var index = (int)createResult;
            allocatedIndices.Add(index);
            var obj = clientObjMgr->GetObjectByIndex((ushort)index);
            if (obj == null)
            {
                log.Error($"GetObjectByIndex returned null for index {index}.");
                OnSpawnError?.Invoke($"Object null at index {index} after creation.");
                return;
            }

            var chara = (BattleChara*)obj;
            var character = (Character*)chara;
            log.Info($"Got BattleChara at index {index}, address=0x{(nint)chara:X}");

            // Step 2: Configure properties
            obj->ObjectKind = ObjectKind.BattleNpc;
            obj->SubKind = (byte)BattleNpcSubKind.Combatant;
            obj->TargetableStatus = 0; // Untargetable during setup

            // Position, rotation
            obj->Position = spawnPos;
            obj->Rotation = spawnRot;

            // Step 3: Set name
            var nameBytes = Encoding.UTF8.GetBytes(npcName);
            for (int j = 0; j < 64; j++)
                obj->Name[j] = j < nameBytes.Length && j < 63 ? nameBytes[j] : (byte)0;

            // Step 4: RenderFlags = 0 (needed for actor VFX)
            obj->RenderFlags = VisibilityFlags.None;

            // Step 5: Initialize model — either BNpc (monster) or ENpc (humanoid)
            if (request.ENpcBaseId > 0)
            {
                // Humanoid NPC: read customize data from ENpcBase and write to DrawData.
                // ENpcBase stores appearance bytes (race, face, hair, etc.) and equipment.
                ApplyENpcAppearance(character, request.ENpcBaseId);
                log.Info($"Applied ENpc appearance for ENpcBase {request.ENpcBaseId}.");
            }
            else if (request.BNpcBaseId > 0)
            {
                // Monster/creature: use native SetupBNpc API
                character->CharacterSetup.SetupBNpc(request.BNpcBaseId, request.BNpcNameId);
                log.Info($"SetupBNpc({request.BNpcBaseId}, {request.BNpcNameId}) done. ModelCharaId={character->ModelContainer.ModelCharaId}");
            }

            // Step 6: Self-copy to trigger model loading/rendering pipeline.
            character->CharacterSetup.CopyFromCharacter(
                character, CharacterSetupContainer.CopyFlags.None);
            log.Info("CopyFromCharacter self-copy done.");

            // Step 7: Create managed object reference
            IGameObject? gameObjectRef = null;
            try
            {
                gameObjectRef = objectTable.CreateObjectReference((nint)obj);
                log.Info($"CreateObjectReference: {(gameObjectRef != null ? "success" : "null")}");
            }
            catch (Exception ex)
            {
                log.Warning(ex, "CreateObjectReference failed (non-fatal).");
            }

            // Set entity ID — write to game object so native systems
            // (ActionEffectHandler.Receive, FindCharacter, etc.) can resolve it.
            // Without this, EntityId defaults to 0xE0000000 and native combat
            // animations (hit reactions, damage numbers) won't find the target.
            var entityId = nextEntityId++;
            obj->EntityId = entityId;

            // Calculate HP
            int maxHp = CalculateNpcHp(npcLevel, request.HpMultiplier);

            var npc = new SimulatedNpc
            {
                SimulatedEntityId = entityId,
                ObjectIndex = index,
                Name = npcName,
                BattleChara = chara,
                GameObjectRef = gameObjectRef,
                SpawnPosition = spawnPos,
                Behavior = NpcBehavior.Create(request.BehaviorType),
                IsSpawned = false, // Will become true when draw is enabled
                State = new SimulatedEntityState
                {
                    EntityId = entityId,
                    Name = npcName,
                    IsPlayer = false,
                    Level = npcLevel,
                    MaxHp = maxHp,
                    CurrentHp = maxHp,
                    MaxMp = 10000,
                    CurrentMp = 10000,
                    MainStat = 100 + npcLevel * 10,
                    Defense = 100 + npcLevel * 5,
                    MagicDefense = 100 + npcLevel * 5,
                },
            };

            // Step 8: Add to pending - delay EnableDraw to next frame
            // (RaidsRewritten does this to allow file replacements to run)
            pendingSpawns.Add(new PendingSpawn { Npc = npc, FramesWaited = 0 });
            log.Info($"NPC '{npcName}' created at index {index}, entityId={entityId:X}. Pending draw...");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Exception in ProcessSpawnRequest.");
            OnSpawnError?.Invoke($"Exception: {ex.Message}");
        }
    }

    private void TickPendingSpawns()
    {
        for (int i = pendingSpawns.Count - 1; i >= 0; i--)
        {
            var pending = pendingSpawns[i];
            var npc = pending.Npc;

            if (npc.BattleChara == null)
            {
                log.Warning($"Pending NPC '{npc.Name}' lost BattleChara pointer.");
                pendingSpawns.RemoveAt(i);
                OnSpawnError?.Invoke($"NPC '{npc.Name}' lost reference.");
                continue;
            }

            pending.FramesWaited++;

            try
            {
                var chara = npc.BattleChara;

                if (chara->IsReadyToDraw())
                {
                    chara->EnableDraw();

                    // Now make targetable (was 0 during setup)
                    var obj = (GameObject*)chara;
                    obj->TargetableStatus = ObjectTargetableFlags.IsTargetable;

                    npc.IsSpawned = true;
                    spawnedNpcs.Add(npc);
                    pendingSpawns.RemoveAt(i);

                    log.Info($"NPC '{npc.Name}' draw enabled after {pending.FramesWaited} frames.");
                    OnNpcSpawnComplete?.Invoke(npc);
                }
                else if (pending.FramesWaited >= MaxPendingFrames)
                {
                    // Timeout - force enable draw
                    log.Warning($"NPC '{npc.Name}' timed out after {pending.FramesWaited} frames. Force enabling.");
                    chara->EnableDraw();

                    var obj = (GameObject*)chara;
                    obj->TargetableStatus = ObjectTargetableFlags.IsTargetable;

                    npc.IsSpawned = true;
                    spawnedNpcs.Add(npc);
                    pendingSpawns.RemoveAt(i);

                    OnNpcSpawnComplete?.Invoke(npc);
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Error during pending spawn for '{npc.Name}'.");
                pendingSpawns.RemoveAt(i);
                OnSpawnError?.Invoke($"'{npc.Name}' draw failed: {ex.Message}");
            }
        }
    }

    public void DespawnNpc(SimulatedNpc npc)
    {
        if (!npc.IsSpawned)
        {
            pendingSpawns.RemoveAll(p => p.Npc == npc);
            return;
        }

        try
        {
            var clientObjMgr = ClientObjectManager.Instance();
            if (clientObjMgr != null && npc.ObjectIndex >= 0)
            {
                if (npc.BattleChara != null)
                {
                    var obj = (GameObject*)npc.BattleChara;
                    obj->DisableDraw();
                }

                clientObjMgr->DeleteObjectByIndex((ushort)npc.ObjectIndex, 0);
            }

            npc.BattleChara = null;
            npc.IsSpawned = false;
            spawnedNpcs.Remove(npc);
            allocatedIndices.Remove(npc.ObjectIndex);

            log.Info($"Despawned NPC '{npc.Name}' from index {npc.ObjectIndex}.");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed to despawn NPC '{npc.Name}'.");
            npc.IsSpawned = false;
            spawnedNpcs.Remove(npc);
        }
    }

    public void DespawnAll()
    {
        while (spawnQueue.TryDequeue(out _)) { }

        foreach (var pending in pendingSpawns)
        {
            try
            {
                var clientObjMgr = ClientObjectManager.Instance();
                if (clientObjMgr != null && pending.Npc.ObjectIndex >= 0)
                    clientObjMgr->DeleteObjectByIndex((ushort)pending.Npc.ObjectIndex, 0);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Failed to clean up pending NPC '{pending.Npc.Name}'.");
            }
        }
        pendingSpawns.Clear();
        allocatedIndices.Clear();

        var npcsToRemove = new List<SimulatedNpc>(spawnedNpcs);
        foreach (var npc in npcsToRemove)
            DespawnNpc(npc);
    }

    /// <summary>
    /// Read ENpcBase customize data and equipment, write to character's DrawData.
    /// ENpcBase stores humanoid appearance at known offsets (from Anamnesis research):
    /// customize bytes at 202-227, equipment models at 128-184, NpcEquip ref at 192.
    /// </summary>
    private void ApplyENpcAppearance(Character* character, uint eNpcBaseId)
    {
        var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ENpcBase>();
        if (sheet == null) return;

        var row = sheet.GetRowOrDefault(eNpcBaseId);
        if (row == null) return;

        var enpc = row.Value;

        // Read ModelChara — if > 0, this ENpc uses a monster model, not humanoid
        var modelCharaId = (int)enpc.ModelChara.RowId;
        if (modelCharaId > 0)
        {
            character->ModelContainer.ModelCharaId = modelCharaId;
            log.Info($"ENpc {eNpcBaseId}: monster model ModelCharaId={modelCharaId}");
            return;
        }

        // Humanoid: write customize data to DrawData.
        // CustomizeData is a 26-byte struct at a known layout matching ENpcBase offsets.
        // Write directly via pointer for the packed bit-field bytes.
        var customizePtr = (byte*)&character->DrawData.CustomizeData;
        customizePtr[0x00] = (byte)enpc.Race.RowId;       // Race
        customizePtr[0x01] = enpc.Gender;                   // Sex
        customizePtr[0x02] = enpc.BodyType;                 // BodyType
        customizePtr[0x03] = enpc.Height;                   // Height
        customizePtr[0x04] = (byte)enpc.Tribe.RowId;       // Tribe
        customizePtr[0x05] = enpc.Face;                     // Face
        customizePtr[0x06] = enpc.HairStyle;                // Hairstyle
        customizePtr[0x07] = enpc.HairHighlight;            // Highlights (bit 7 = enabled)
        customizePtr[0x08] = enpc.SkinColor;                // SkinColor
        customizePtr[0x09] = enpc.EyeHeterochromia;         // EyeColorRight
        customizePtr[0x0A] = enpc.HairColor;                // HairColor
        customizePtr[0x0B] = enpc.HairHighlightColor;       // HighlightsColor
        customizePtr[0x0C] = enpc.FacialFeature;            // FacialFeatures (packed bits)
        customizePtr[0x0D] = enpc.FacialFeatureColor;       // TattooColor
        customizePtr[0x0E] = enpc.Eyebrows;                 // Eyebrows
        customizePtr[0x0F] = enpc.EyeColor;                 // EyeColorLeft
        customizePtr[0x10] = enpc.EyeShape;                 // EyeShape (packed)
        customizePtr[0x11] = enpc.Nose;                     // Nose
        customizePtr[0x12] = enpc.Jaw;                      // Jaw
        customizePtr[0x13] = enpc.Mouth;                    // Mouth (packed)
        customizePtr[0x14] = enpc.LipColor;                 // LipColorFurPattern
        customizePtr[0x15] = enpc.BustOrTone1;              // MuscleMass
        customizePtr[0x16] = enpc.ExtraFeature1;            // TailShape
        customizePtr[0x17] = enpc.ExtraFeature2OrBust;      // BustSize
        customizePtr[0x18] = enpc.FacePaint;                // FacePaint (packed)
        customizePtr[0x19] = enpc.FacePaintColor;           // FacePaintColor

        // Equipment: read from ENpcBase inline model values
        // Head=offset 148, Body=152, Hands=156, Legs=160, Feet=164
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Head).Value =
            (ulong)enpc.ModelHead;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Body).Value =
            (ulong)enpc.ModelBody;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Hands).Value =
            (ulong)enpc.ModelHands;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Legs).Value =
            (ulong)enpc.ModelLegs;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Feet).Value =
            (ulong)enpc.ModelFeet;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Ears).Value =
            (ulong)enpc.ModelEars;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Neck).Value =
            (ulong)enpc.ModelNeck;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Wrists).Value =
            (ulong)enpc.ModelWrists;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.RFinger).Value =
            (ulong)enpc.ModelRightRing;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.LFinger).Value =
            (ulong)enpc.ModelLeftRing;

        // Weapons: MainHand (offset 128) and OffHand (offset 136)
        var mainHand = enpc.ModelMainHand;
        var offHand = enpc.ModelOffHand;
        if (mainHand != 0)
        {
            var weaponId = *(WeaponModelId*)&mainHand;
            character->DrawData.LoadWeapon(DrawDataContainer.WeaponSlot.MainHand, weaponId, 0, 0, 0, 0);
        }
        if (offHand != 0)
        {
            var weaponId = *(WeaponModelId*)&offHand;
            character->DrawData.LoadWeapon(DrawDataContainer.WeaponSlot.OffHand, weaponId, 0, 0, 0, 0);
        }

        log.Info($"ENpc {eNpcBaseId}: humanoid, Race={customizePtr[0]}, Gender={customizePtr[1]}, Face={customizePtr[5]}");
    }

    private string GetNpcName(NpcSpawnRequest request)
    {
        // Try BNpcName first (for BNpc entries)
        if (request.BNpcNameId > 0)
        {
            var nameSheet = dataManager.GetExcelSheet<BNpcName>();
            if (nameSheet != null)
            {
                var nameRow = nameSheet.GetRowOrDefault(request.BNpcNameId);
                if (nameRow != null)
                {
                    var name = nameRow.Value.Singular.ExtractText();
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
        }

        // Try ENpcResident for humanoid NPCs (same RowId as ENpcBase)
        if (request.ENpcBaseId > 0)
        {
            var residentSheet = dataManager.GetExcelSheet<ENpcResident>();
            if (residentSheet != null)
            {
                var row = residentSheet.GetRowOrDefault(request.ENpcBaseId);
                if (row != null)
                {
                    var name = row.Value.Singular.ExtractText();
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
        }

        if (request.BNpcBaseId > 0)
            return $"Enemy #{request.BNpcBaseId}";
        if (request.ENpcBaseId > 0)
            return $"NPC #{request.ENpcBaseId}";

        return "Simulated Enemy";
    }

    private Vector3 CalculateSpawnPosition()
    {
        var player = objectTable[0];
        if (player != null)
        {
            var playerPos = player.Position;
            var playerRot = player.Rotation;
            var forward = new Vector3(
                -MathF.Sin(playerRot),
                0,
                -MathF.Cos(playerRot));
            return playerPos + forward * 5.0f;
        }

        return Vector3.Zero;
    }

    private float CalculateSpawnRotation(Vector3 spawnPos)
    {
        var player = objectTable[0];
        if (player != null)
        {
            var dir = player.Position - spawnPos;
            return MathF.Atan2(dir.X, dir.Z);
        }
        return 0;
    }

    private int CalculateNpcHp(int level, float multiplier)
    {
        int baseHp = level switch
        {
            <= 10 => 200 + level * 50,
            <= 30 => 500 + level * 150,
            <= 50 => 2000 + level * 500,
            <= 70 => 10000 + level * 1000,
            <= 80 => 30000 + level * 2000,
            <= 90 => 80000 + level * 3000,
            _ => 150000 + level * 5000,
        };

        return (int)(baseHp * multiplier);
    }

    public void Dispose()
    {
        DespawnAll();
    }

    private class PendingSpawn
    {
        public SimulatedNpc Npc { get; set; } = null!;
        public int FramesWaited { get; set; }
    }
}
