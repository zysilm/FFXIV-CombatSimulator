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
    private readonly IClientState clientState;
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

    public NpcSpawner(
        IObjectTable objectTable,
        IDataManager dataManager,
        IClientState clientState,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.clientState = clientState;
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
                // Humanoid path (Brio-style clone-from-player bootstrap).
                // There is NO native SetupENpc API — the only way to initialize
                // a working humanoid Character is to CopyFromCharacter() from an
                // already-working humanoid, then overwrite appearance on top.
                // Without this, Mode/ClassJob/TimelineContainer are uninitialized
                // and the game refuses to run humanoid draw/animation logic, so
                // the NPC has no facing update, no battle stance, and no walk anim.
                if (!BootstrapHumanoidFromPlayer(character, request.ENpcBaseId))
                {
                    log.Error($"[SpawnDbg] Humanoid bootstrap failed for ENpcBase {request.ENpcBaseId}. Aborting spawn.");
                    // Roll back the allocation so the slot is reusable
                    clientObjMgr->DeleteObjectByIndex((ushort)index, 0);
                    allocatedIndices.Remove(index);
                    OnSpawnError?.Invoke("Humanoid spawn needs a valid local player as clone source.");
                    return;
                }
            }
            else if (request.BNpcBaseId > 0)
            {
                // Monster/creature: use native SetupBNpc API
                character->CharacterSetup.SetupBNpc(request.BNpcBaseId, request.BNpcNameId);
                log.Info($"[SpawnDbg] SetupBNpc({request.BNpcBaseId}, {request.BNpcNameId}) done. ModelCharaId={character->ModelContainer.ModelCharaId}");
            }

            // Step 6: Self-copy to trigger the DrawData refresh pipeline.
            // Brio's "double copy" pattern — the first CopyFromCharacter populates
            // the target (for BNpc it's SetupBNpc that already did the work; for
            // humanoids it's BootstrapHumanoidFromPlayer above). This self-copy is
            // the trigger that tools like Glamourer/Penumbra expect to see.
            character->CharacterSetup.CopyFromCharacter(
                character, CharacterSetupContainer.CopyFlags.None);
            log.Info("[SpawnDbg] Self-copy CopyFromCharacter(self, None) done.");

            // Step 6b: Force Mode to Normal so the walk/run animation state machine
            // is active. CreateBattleCharacter leaves Mode at 0 (None) which makes
            // the game skip all animation dispatch for that character.
            var preModeDbg = character->Mode;
            character->SetMode(CharacterModes.Normal, 0);
            log.Info($"[SpawnDbg] SetMode Normal done (was {preModeDbg}, now {character->Mode}).");

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

            // Read weapon data for deferred loading after EnableDraw
            ulong mainHandWeapon = 0, offHandWeapon = 0;
            if (request.ENpcBaseId > 0)
            {
                var eSheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ENpcBase>();
                var eRow = eSheet?.GetRowOrDefault(request.ENpcBaseId);
                if (eRow != null)
                {
                    mainHandWeapon = eRow.Value.ModelMainHand;
                    offHandWeapon = eRow.Value.ModelOffHand;
                }
            }

            // Step 8: Add to pending - delay EnableDraw to next frame
            // (RaidsRewritten does this to allow file replacements to run)
            pendingSpawns.Add(new PendingSpawn
            {
                Npc = npc,
                FramesWaited = 0,
                MainHandWeapon = mainHandWeapon,
                OffHandWeapon = offHandWeapon,
            });
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
                    var character = (Character*)chara;
                    log.Info($"[SpawnDbg] '{npc.Name}' IsReadyToDraw=true @ frame {pending.FramesWaited}. Mode={character->Mode}, ClassJob={character->CharacterData.ClassJob}, ModelCharaId={character->ModelContainer.ModelCharaId}");

                    chara->EnableDraw();
                    log.Info($"[SpawnDbg] '{npc.Name}' EnableDraw called. DrawObject={(nint)((GameObject*)chara)->DrawObject:X}");

                    // Load weapons AFTER EnableDraw (self-copy resets DrawData weapons)
                    LoadPendingWeapons(chara, pending);

                    // Now make targetable (was 0 during setup)
                    var obj = (GameObject*)chara;
                    obj->TargetableStatus = ObjectTargetableFlags.IsTargetable;

                    npc.IsSpawned = true;
                    spawnedNpcs.Add(npc);
                    pendingSpawns.RemoveAt(i);

                    log.Info($"[SpawnDbg] '{npc.Name}' draw enabled after {pending.FramesWaited} frames.");
                    OnNpcSpawnComplete?.Invoke(npc);
                }
                else if (pending.FramesWaited >= MaxPendingFrames)
                {
                    // Timeout - force enable draw
                    log.Warning($"NPC '{npc.Name}' timed out after {pending.FramesWaited} frames. Force enabling.");
                    chara->EnableDraw();
                    LoadPendingWeapons(chara, pending);

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

    private void LoadPendingWeapons(BattleChara* chara, PendingSpawn pending)
    {
        if (pending.MainHandWeapon == 0 && pending.OffHandWeapon == 0) return;

        try
        {
            var character = (Character*)chara;

            // Both hands must be loaded together for the weapon system to initialize
            var mh = pending.MainHandWeapon;
            var oh = pending.OffHandWeapon;
            var mhId = *(WeaponModelId*)&mh;
            var ohId = *(WeaponModelId*)&oh;

            character->DrawData.LoadWeapon(DrawDataContainer.WeaponSlot.MainHand, mhId, 0, 0, 0, 0);
            character->DrawData.LoadWeapon(DrawDataContainer.WeaponSlot.OffHand, ohId, 0, 0, 0, 0);

            log.Verbose($"Loaded weapons: MH={mhId.Id}/{mhId.Type}/{mhId.Variant}, OH={ohId.Id}/{ohId.Type}/{ohId.Variant}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to load weapons for '{pending.Npc.Name}'.");
        }
    }

    /// <summary>
    /// Clone the local player's Character onto the freshly-created BattleChara so
    /// the game's humanoid pipeline is fully initialized (Mode / ClassJob / draw
    /// object / Timeline state), then overwrite customize bytes and equipment
    /// from the ENpcBase row. This is Brio's spawn pattern — the only known way
    /// to get a spawned humanoid to actually animate.
    ///
    /// Returns false if the local player isn't a valid clone source (e.g. we're
    /// between zones, in a cutscene, dead, or not logged in).
    /// </summary>
    private bool BootstrapHumanoidFromPlayer(Character* target, uint eNpcBaseId)
    {
        // First verify the ENpcBase actually represents a humanoid.
        // If ModelChara > 0 this ENpc uses a monster model and should not
        // go through the clone path at all — fall back to ModelCharaId set.
        var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ENpcBase>();
        if (sheet == null)
        {
            log.Error("[SpawnDbg] ENpcBase sheet not available.");
            return false;
        }
        var row = sheet.GetRowOrDefault(eNpcBaseId);
        if (row == null)
        {
            log.Error($"[SpawnDbg] ENpcBase {eNpcBaseId} not found.");
            return false;
        }
        var enpc = row.Value;
        var modelCharaId = (int)enpc.ModelChara.RowId;
        if (modelCharaId > 0)
        {
            // Monster-model ENpc — no clone needed. Treat like a BNpc model set.
            target->ModelContainer.ModelCharaId = modelCharaId;
            log.Info($"[SpawnDbg] ENpc {eNpcBaseId}: monster model, ModelCharaId={modelCharaId}");
            return true;
        }

        // Humanoid ENpc path — need a valid local player as clone source.
        var localPlayer = clientState.LocalPlayer;
        if (localPlayer == null || localPlayer.Address == nint.Zero)
        {
            log.Error("[SpawnDbg] Local player not available as clone source.");
            return false;
        }

        var source = (Character*)localPlayer.Address;

        // Sanity: make sure the source is itself a humanoid (has Race > 0 and
        // ModelCharaId == 0). If the player is currently in a monster form —
        // say, fantasia'd or on a special mount — this would blow up.
        var sourceRace = ((byte*)&source->DrawData.CustomizeData)[0];
        if (sourceRace == 0)
        {
            log.Error($"[SpawnDbg] Local player has Race=0 (not humanoid right now). Cannot use as clone source.");
            return false;
        }

        log.Info($"[SpawnDbg] Cloning humanoid from local player addr=0x{(nint)source:X}, sourceRace={sourceRace}");

        // Step A: Full clone from player. Include ClassJob (animation timelines
        // are gated on having a valid ClassJob) and WeaponHiding (matches Brio).
        // We deliberately do NOT copy Mode — that's an Emote loop flag and we'll
        // force Mode=Normal explicitly later. We do NOT copy Position/Target/Name
        // either; those are set manually in ProcessSpawnRequest.
        const CharacterSetupContainer.CopyFlags flags =
            CharacterSetupContainer.CopyFlags.ClassJob |
            CharacterSetupContainer.CopyFlags.WeaponHiding;
        target->CharacterSetup.CopyFromCharacter(source, flags);
        log.Info($"[SpawnDbg] CopyFromCharacter(player, ClassJob|WeaponHiding) done.");

        // Step B: Overwrite customize bytes + equipment from ENpcBase so the
        // cloned player actually looks like the requested NPC. Weapons are
        // loaded later via LoadPendingWeapons after EnableDraw.
        OverwriteCustomizeFromENpc(target, enpc);

        return true;
    }

    /// <summary>
    /// Overwrite the 26 customize bytes + 10 equipment slots from an ENpcBase row.
    /// Called after BootstrapHumanoidFromPlayer so the base Character already has
    /// a working humanoid pipeline from the clone.
    /// </summary>
    private void OverwriteCustomizeFromENpc(Character* character, Lumina.Excel.Sheets.ENpcBase enpc)
    {
        var customizePtr = (byte*)&character->DrawData.CustomizeData;
        customizePtr[0x00] = (byte)enpc.Race.RowId;
        customizePtr[0x01] = enpc.Gender;

        // BodyType is the child/adult discriminator at draw time. ENpcBase
        // BodyType=3 produces RaceSexId=c0X04 (child) which has no walk /
        // battle-stance / emote animations. Force it to 1 (standard adult)
        // so the derivation gives the adult model path. This is the only
        // byte we deliberately override — everything else comes from ENpcBase.
        byte bodyType = enpc.BodyType == 1 ? enpc.BodyType : (byte)1;
        if (bodyType != enpc.BodyType)
            log.Info($"[SpawnDbg] Child body detected (ENpc BodyType={enpc.BodyType}), forcing BodyType=1 (adult) for animation support.");
        customizePtr[0x02] = bodyType;

        customizePtr[0x03] = enpc.Height;
        customizePtr[0x04] = (byte)enpc.Tribe.RowId;
        customizePtr[0x05] = enpc.Face;
        customizePtr[0x06] = enpc.HairStyle;
        customizePtr[0x07] = enpc.HairHighlight;
        customizePtr[0x08] = enpc.SkinColor;
        customizePtr[0x09] = enpc.EyeHeterochromia;
        customizePtr[0x0A] = enpc.HairColor;
        customizePtr[0x0B] = enpc.HairHighlightColor;
        customizePtr[0x0C] = enpc.FacialFeature;
        customizePtr[0x0D] = enpc.FacialFeatureColor;
        customizePtr[0x0E] = enpc.Eyebrows;
        customizePtr[0x0F] = enpc.EyeColor;
        customizePtr[0x10] = enpc.EyeShape;
        customizePtr[0x11] = enpc.Nose;
        customizePtr[0x12] = enpc.Jaw;
        customizePtr[0x13] = enpc.Mouth;
        customizePtr[0x14] = enpc.LipColor;
        customizePtr[0x15] = enpc.BustOrTone1;
        customizePtr[0x16] = enpc.ExtraFeature1;
        customizePtr[0x17] = enpc.ExtraFeature2OrBust;
        customizePtr[0x18] = enpc.FacePaint;
        customizePtr[0x19] = enpc.FacePaintColor;

        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Head).Value = (ulong)enpc.ModelHead;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Body).Value = (ulong)enpc.ModelBody;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Hands).Value = (ulong)enpc.ModelHands;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Legs).Value = (ulong)enpc.ModelLegs;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Feet).Value = (ulong)enpc.ModelFeet;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Ears).Value = (ulong)enpc.ModelEars;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Neck).Value = (ulong)enpc.ModelNeck;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Wrists).Value = (ulong)enpc.ModelWrists;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.RFinger).Value = (ulong)enpc.ModelRightRing;
        character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.LFinger).Value = (ulong)enpc.ModelLeftRing;

        log.Info($"[SpawnDbg] Overwrote customize/equipment from ENpcBase: Race={customizePtr[0]}, Tribe={customizePtr[4]}, Gender={customizePtr[1]}, Face={customizePtr[5]}, Body=0x{(ulong)enpc.ModelBody:X}");
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
        public ulong MainHandWeapon { get; set; }
        public ulong OffHandWeapon { get; set; }
    }
}
