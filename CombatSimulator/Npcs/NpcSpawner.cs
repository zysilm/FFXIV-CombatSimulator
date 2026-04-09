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
            var npcName = GetNpcName(request.BNpcNameId, request.BNpcBaseId);
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

            // Get local player for initial character setup
            var localPlayer = objectTable[0];
            if (localPlayer == null)
            {
                log.Error("Local player is null.");
                OnSpawnError?.Invoke("Local player not found. Are you logged in?");
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

            // Step 5: Initialize rendering by copying from local player (Brio approach)
            // This sets up valid character data so the rendering pipeline can work.
            var playerChara = (Character*)localPlayer.Address;
            character->CharacterSetup.CopyFromCharacter(
                playerChara, CharacterSetupContainer.CopyFlags.None);
            log.Info("CopyFromCharacter from local player done.");

            // Step 6: Use SetupBNpc to properly initialize the NPC model
            // This is the native FFXIV API that handles ModelCharaId, scale, and model loading.
            if (request.BNpcBaseId > 0)
            {
                character->CharacterSetup.SetupBNpc(request.BNpcBaseId, request.BNpcNameId);
                log.Info($"SetupBNpc({request.BNpcBaseId}, {request.BNpcNameId}) done.");
            }

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

                // Match RaidsRewritten: check IsReadyToDraw then EnableDraw
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

    private string GetNpcName(uint bNpcNameId, uint bNpcBaseId)
    {
        if (bNpcNameId > 0)
        {
            var nameSheet = dataManager.GetExcelSheet<BNpcName>();
            if (nameSheet != null)
            {
                var nameRow = nameSheet.GetRowOrDefault(bNpcNameId);
                if (nameRow != null)
                {
                    var name = nameRow.Value.Singular.ExtractText();
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
        }

        if (bNpcBaseId > 0)
            return $"Enemy #{bNpcBaseId}";

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
