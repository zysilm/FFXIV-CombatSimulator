using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Integration;
using CombatSimulator.Safety;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator.Dev;

/// <summary>
/// Hidden dev mode: "Monster". On player death, spawns a controllable creature (default Bat,
/// a BNpc model) with no HP. The player flies it around with the keyboard; the camera follows
/// the creature (active-camera-style free orbit centered on it); and the normal attack input
/// (keyboard or gamepad — routed via the UseAction hook) punts the ground ragdoll with an
/// adjustable physics impulse.
///
/// Controls:
///   W / S — forward / back along facing,  A / D — turn,  Q / E — ascend / descend (ground-clamped)
///   Attack action (your real hotkey/gamepad button) — punt the ragdoll
///
/// Position/rotation are driven via MovementBlockHook (so the game's own movement doesn't fight
/// our writes). No AI, not targetable.
/// </summary>
public unsafe class MonsterModeController : IDisposable
{
    private readonly IKeyState keyState;
    private readonly IGamepadState gamepad;
    private readonly IFramework framework;
    private readonly RagdollController playerRagdoll;
    private readonly AnimationController animation;
    private readonly BoneTransformService boneService;
    private readonly MovementBlockHook movementBlock;
    private readonly ActiveCameraController activeCamera;
    private readonly VNavmeshIpc vnavmesh;
    private readonly DismembermentController dismemberment;
    private readonly GlamourerIpc glamourer;
    private readonly Configuration config;
    private readonly IPluginLog log;

    // Progressive part-separation state + delayed "connecting hit" feedback timers.
    private readonly MonsterDismembermentPlanner partPlanner = new();
    private readonly List<float> pendingHitTimers = new();

    // Optional held-corpse mode: once everything is peeled, a further strike releases the hold.
    private BoneHoldTestModeController? holdController;
    public void AttachHold(BoneHoldTestModeController hold) => holdController = hold;

    private int monsterIndex = -1;       // ClientObjectManager index (only when we spawned the object)
    private uint monsterEntityId;
    private nint monsterAddress;
    private bool ownsObject;              // true = we spawned it (delete on despawn); false = controlling an existing enemy
    private Npcs.SimulatedNpc? controlledNpc; // the killer we took over (so we can release its AI)
    private bool pendingDraw;
    private int framesWaited;
    private const int MaxPendingFrames = 60;

    private float yaw;
    private float posX, posY, posZ;
    private bool prevActiveCamState;
    private bool prevAttackKeyDown;
    private bool colliderRegistered;
    private readonly ActorVisualState visualState = new();

    public bool IsActive => monsterAddress != nint.Zero;

    /// <summary>True while we're controlling an existing enemy at this address (suppresses its AI).</summary>
    public bool ControlsNpc(nint address) => controlledNpc != null && controlledNpc.Address == address;

    public MonsterModeController(IKeyState keyState, IGamepadState gamepad, IFramework framework,
        RagdollController playerRagdoll, AnimationController animation, BoneTransformService boneService,
        MovementBlockHook movementBlock, ActiveCameraController activeCamera,
        VNavmeshIpc vnavmesh, DismembermentController dismemberment, GlamourerIpc glamourer,
        Configuration config, IPluginLog log)
    {
        this.keyState = keyState;
        this.gamepad = gamepad;
        this.framework = framework;
        this.playerRagdoll = playerRagdoll;
        this.animation = animation;
        this.boneService = boneService;
        this.movementBlock = movementBlock;
        this.activeCamera = activeCamera;
        this.vnavmesh = vnavmesh;
        this.dismemberment = dismemberment;
        this.glamourer = glamourer;
        this.config = config;
        this.log = log;
        framework.Update += OnUpdate;
    }

    // Center the camera on the creature's body, not its origin (feet). Try common center
    // bones in order; fall back to the object position raised a little.
    private static readonly string[] CameraCenterBones = { "j_kosi", "n_hara", "j_sebo_a", "j_sebo_b", "j_kao" };

    /// <summary>Orbit center for the active camera while the monster is alive (a body bone).</summary>
    private Vector3? CameraCenter()
    {
        if (!IsActive) return null;
        foreach (var b in CameraCenterBones)
        {
            var p = boneService.GetBoneWorldPos(monsterAddress, b);
            if (p.HasValue) return p.Value;
        }
        return new Vector3(posX, posY + 0.5f, posZ);
    }

    /// <summary>True when the active camera is orbiting the creature; false = orbiting the player.</summary>
    public bool CameraFollowsMonster => config.MonsterCameraFollowsMonster;

    /// <summary>Toggle the active camera between following the monster and the player (character cam).
    /// The choice is remembered (config) — spawn/despawn/reset never auto-switch it.</summary>
    public void ToggleCamera()
    {
        if (!IsActive) return;
        config.MonsterCameraFollowsMonster = !config.MonsterCameraFollowsMonster;
        config.Save();
        // Override → monster; null → active camera falls back to the player's bone.
        activeCamera.GetOrbitCenterOverride = config.MonsterCameraFollowsMonster ? CameraCenter : null;
    }

    public void Spawn()
    {
        if (IsActive || pendingDraw) { log.Info("MonsterMode: already active — despawn first"); return; }

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) { log.Warning("MonsterMode: no local player"); return; }

        var mgr = ClientObjectManager.Instance();
        if (mgr == null) { log.Warning("MonsterMode: ClientObjectManager null"); return; }

        var hint = FindFreeObjectHint(mgr);
        var createResult = mgr->CreateBattleCharacter(hint);
        if (createResult == 0xFFFFFFFF) { log.Warning("MonsterMode: CreateBattleCharacter failed (no slot)"); return; }

        var index = (int)createResult;
        var obj = mgr->GetObjectByIndex((ushort)index);
        if (obj == null) { log.Warning($"MonsterMode: object null at index {index}"); return; }

        var chara = (BattleChara*)obj;
        var character = (Character*)chara;

        obj->ObjectKind = ObjectKind.BattleNpc;
        obj->SubKind = (byte)BattleNpcSubKind.Combatant;
        obj->TargetableStatus = 0;
        obj->RenderFlags = VisibilityFlags.None;

        var spawnPos = player.Position;
        yaw = player.Rotation;
        posX = spawnPos.X; posY = spawnPos.Y; posZ = spawnPos.Z;
        obj->Position = spawnPos;
        obj->Rotation = yaw;

        var nameBytes = Encoding.UTF8.GetBytes("Monster");
        for (int j = 0; j < 64; j++)
            obj->Name[j] = j < nameBytes.Length && j < 63 ? nameBytes[j] : (byte)0;

        character->CharacterSetup.SetupBNpc(config.MonsterModelId, config.MonsterModelNameId);
        character->CharacterSetup.CopyFromCharacter(character, CharacterSetupContainer.CopyFlags.None);

        obj->ObjectKind = ObjectKind.BattleNpc;
        obj->SubKind = (byte)BattleNpcSubKind.Combatant;
        character->SetMode(CharacterModes.Normal, 0);

        // A valid EntityId lets it be the caster of a fabricated attack ActionEffect
        // (so the swing animation + sound play).
        monsterEntityId = 0xF0000000u | (uint)index;
        obj->EntityId = monsterEntityId;

        monsterIndex = index;
        monsterAddress = (nint)obj;
        ownsObject = true;
        pendingDraw = true;
        framesWaited = 0;
        BeginControl(spawnPos, yaw);

        log.Info($"MonsterMode: spawned model={config.MonsterModelId} at index {index} (0x{monsterAddress:X})");
    }

    /// <summary>
    /// Take control of an existing enemy (the one that just defeated the player) instead of
    /// spawning a creature. Same controls; we don't own the object (no delete on release).
    /// </summary>
    public void ControlKiller(Npcs.SimulatedNpc killer)
    {
        if (IsActive) { log.Info("MonsterMode: already active"); return; }
        if (killer.BattleChara == null || killer.Address == nint.Zero) { log.Warning("MonsterMode: killer invalid"); return; }

        var obj = (GameObject*)killer.Address;
        if (obj->DrawObject == null) { log.Warning("MonsterMode: killer not drawn"); return; }

        controlledNpc = killer;
        killer.IsClientControlled = true; // suppress its AI behaviours
        monsterIndex = -1;
        ownsObject = false;
        monsterAddress = killer.Address;
        monsterEntityId = obj->EntityId;
        pendingDraw = false; // already drawn
        var pos = new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z);
        posX = pos.X; posY = pos.Y; posZ = pos.Z;
        yaw = obj->Rotation;
        BeginControl(pos, yaw);

        log.Info($"MonsterMode: controlling killer '{killer.Name}' (0x{monsterAddress:X})");
    }

    private void BeginControl(Vector3 pos, float rot)
    {
        posX = pos.X; posY = pos.Y; posZ = pos.Z; yaw = rot;
        partPlanner.Reset();
        pendingHitTimers.Clear();
        // Block the game from moving the actor so our writes win.
        movementBlock.AddApproachNpc(monsterAddress);
        // Camera follows per the remembered preference (monster vs character) — don't force it.
        prevActiveCamState = activeCamera.IsActive;
        activeCamera.GetOrbitCenterOverride = config.MonsterCameraFollowsMonster ? CameraCenter : null;
        activeCamera.SetActive(true);
    }

    private void OnUpdate(IFramework fw)
    {
        try
        {
            if (pendingDraw)
            {
                if (monsterAddress == nint.Zero) { pendingDraw = false; return; }
                var chara = (BattleChara*)monsterAddress;
                framesWaited++;
                if (chara->IsReadyToDraw() || framesWaited >= MaxPendingFrames)
                {
                    chara->EnableDraw();
                    pendingDraw = false;
                    log.Info($"MonsterMode: draw enabled after {framesWaited} frames — controls live");
                }
                return;
            }

            if (!IsActive || monsterAddress == nint.Zero) return;

            // Register the creature as a live collider so it physically pushes the ragdoll when it
            // walks into it (not just on attack). Retries until the player ragdoll physics is ready.
            if (!colliderRegistered && playerRagdoll.AddLiveCollider(monsterAddress))
                colliderRegistered = true;

            if (ImGui.GetIO().WantTextInput) return;

            var dt = (float)fw.UpdateDelta.TotalSeconds;
            if (dt <= 0f || dt > 0.25f) dt = 1f / 60f;

            TickMovement(dt);
            TickAttackKey();
            TickPendingHits(dt);
        }
        catch (Exception ex)
        {
            log.Error(ex, "MonsterMode: error in update");
        }
    }

    private void TickMovement(float dt)
    {
        var obj = (GameObject*)monsterAddress;
        var character = (Character*)monsterAddress;

        // ── Horizontal input: keyboard WASD + gamepad left stick (camera-relative) ──
        // Screen axes: +fwd = forward (W reversed earlier), +strafe = right.
        var fwd = 0f; var strafe = 0f;
        if (Down(VirtualKey.W)) fwd += 1f;
        if (Down(VirtualKey.S)) fwd -= 1f;
        if (Down(VirtualKey.D)) strafe += 1f;
        if (Down(VirtualKey.A)) strafe -= 1f;
        // Gamepad left stick via Dalamud's live LeftStick (raw -99..99; the game's axes are
        // sign-inverted: +X = left, +Y = up). Reading our own GamepadInputAddress gave a stale
        // constant, so use Dalamud's accessor. Deadzone kills any rest drift.
        var ls = gamepad.LeftStick;
        var sx = ls.X / 99f; // right positive
        var sy = ls.Y / 99f; // forward/up positive
        const float deadzone = 0.15f;
        if (MathF.Abs(sx) > deadzone) strafe += sx;
        if (MathF.Abs(sy) > deadzone) fwd += sy;

        var dirH = yaw;
        var camMgr = GameCameraManager.Instance();
        if (camMgr != null && camMgr->Camera != null) dirH = camMgr->Camera->DirH;

        // W goes into the screen (forward), so forward = -(sin,cos).
        var forward = new Vector3(-MathF.Sin(dirH), 0f, -MathF.Cos(dirH));
        var right = new Vector3(MathF.Cos(dirH), 0f, -MathF.Sin(dirH));
        var move = forward * fwd + right * strafe;

        var moving = move.LengthSquared() > 0.0001f;
        if (moving)
        {
            if (move.LengthSquared() > 1f) move = Vector3.Normalize(move);
            posX += move.X * config.MonsterMoveSpeed * dt;
            posZ += move.Z * config.MonsterMoveSpeed * dt;
            yaw = MathF.Atan2(move.X, move.Z); // face travel direction
        }

        if (config.MonsterGroundWalk)
        {
            // Walking: no flight — clamp to the floor (navmesh-guided, like the victory approach).
            posY = SnapToFloor(posX, posY, posZ);
        }
        else
        {
            // ── Vertical: keyboard Q/E + gamepad D-pad up/down. Any height, but not below ground. ──
            var up = 0f;
            if (Down(VirtualKey.Q)) up += 1f;
            if (Down(VirtualKey.E)) up -= 1f;
            if (gamepad.Raw(GamepadButtons.R2) != 0) up += 1f; // R2 ascend
            if (gamepad.Raw(GamepadButtons.L2) != 0) up -= 1f; // L2 descend
            posY += up * config.MonsterVerticalSpeed * dt;
            // No ground clamp — the creature may sink below the floor (unlimited), by request.
        }

        if (moving) ActorVisualStateController.ApplyMoving(character, visualState, dt);
        else ActorVisualStateController.ClearMovement(character, visualState);

        // Write through MovementBlockHook so the game's own movement doesn't override us.
        movementBlock.SetApproachPosition(obj, posX, posY, posZ);
        movementBlock.SetApproachRotation(obj, yaw);
    }

    private void TickAttackKey()
    {
        // Keyboard attack key (configurable) + gamepad Cross/South button.
        var down = Down((VirtualKey)config.MonsterAttackKey) || gamepad.Raw(GamepadButtons.South) != 0;
        if (down && !prevAttackKeyDown) OnAttackInput(); // edge trigger
        prevAttackKeyDown = down;
    }

    private const float StrikeWindow = 0.5f; // seconds the strike window stays open (covers the swing)

    /// <summary>Trigger the monster's attack — swing animation + sound; the swinging limb's collider
    /// then flings the ragdoll at the real contact point.</summary>
    public void OnAttackInput()
    {
        if (!IsActive) return;

        // The swing animation moves the monster's colliders; the strike rides on it, so play it first.
        try { PlaySwing(); }
        catch (Exception ex) { log.Warning(ex, "MonsterMode: swing failed"); }

        if (playerRagdoll.IsActive)
            playerRagdoll.BeginAttackStrike(StrikeWindow, config.MonsterStrikePower);

        // A connecting swing (player in melee reach) lands its feedback after the same delay the
        // weapon takes to visually reach the body — see config.HitFeedbackDelay.
        if (PlayerInReach())
            pendingHitTimers.Add(MathF.Max(0f, config.HitFeedbackDelay));
    }

    private const float AttackReach = 3.5f; // melee reach (yalms, horizontal) for a connecting hit

    private bool PlayerInReach()
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero) return false;
        var pp = player.Position;
        var dx = posX - pp.X;
        var dz = posZ - pp.Z;
        return dx * dx + dz * dz <= AttackReach * AttackReach;
    }

    /// <summary>Count down scheduled connecting hits and fire their feedback when the swing lands.</summary>
    private void TickPendingHits(float dt)
    {
        for (int i = pendingHitTimers.Count - 1; i >= 0; i--)
        {
            var t = pendingHitTimers[i] - dt;
            if (t > 0f) { pendingHitTimers[i] = t; continue; }
            pendingHitTimers.RemoveAt(i);
            FireHitFeedback();
        }
    }

    // Mandatory hit spark on the player + optional progressive part separation (per the profile).
    private void FireHitFeedback()
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero) return;

        try { animation.SpawnHitSpark(player.Address); }
        catch (Exception ex) { log.Warning(ex, "MonsterMode: hit spark failed"); }

        var profile = (MonsterStrikePartProfile)config.MonsterStrikePartProfile;
        if (profile == MonsterStrikePartProfile.Off || !playerRagdoll.IsActive) return;

        var bone = partPlanner.NextHit(profile);
        if (bone == null)
        {
            // Fully peeled: if the corpse is being held upright, this strike releases the hold.
            if (holdController != null && holdController.IsActive)
            {
                holdController.Stop();
                log.Info("MonsterMode: fully detached — released hold");
            }
            return;
        }

        // Union with any manually-selected PC dismember bones so SetDismemberedBones (which switches
        // the ragdoll to the local override list) doesn't un-hide those.
        var bones = new List<string>();
        foreach (var b in config.DismemberPocBones)
            if (!string.IsNullOrWhiteSpace(b) && !bones.Contains(b)) bones.Add(b);
        foreach (var b in partPlanner.Severed)
            if (!bones.Contains(b)) bones.Add(b);

        // Body side: collapse the limb subtree so it vanishes from the corpse (same as PC dismember).
        playerRagdoll.SetDismemberedBones(bones);
        // Substitute side: spawn the rolling clone that shows only the severed limb.
        var glam = glamourer.GetStateBase64((int)player.ObjectIndex);
        dismemberment.SyncSelectionFor(player.Address, bones, glam);
        log.Info($"MonsterMode: detached '{bone}' (profile={profile}, total={partPlanner.Severed.Count})");
    }

    /// <summary>Floor-clamp the walking creature: raycast down, then fall back to vnavmesh.</summary>
    private float SnapToFloor(float x, float refY, float z)
    {
        const float rayStart = 2f;
        const float rayDist = 50f;
        if (BGCollisionModule.RaycastMaterialFilter(
                new Vector3(x, refY + rayStart, z), new Vector3(0, -1f, 0), out var hit, rayDist))
            return hit.Point.Y;

        if (vnavmesh != null)
        {
            vnavmesh.RefreshStatus();
            if (vnavmesh.CanPathfind)
            {
                try
                {
                    var floor = vnavmesh.PointOnFloor(new Vector3(x, refY + 10f, z), false, 5f)
                                ?? vnavmesh.NearestPointReachable(new Vector3(x, refY, z));
                    if (floor.HasValue) return floor.Value.Y;
                }
                catch (Exception ex) { log.Verbose($"MonsterMode: floor snap failed ({ex.Message})"); }
            }
        }
        return refY;
    }

    // Fabricate an auto-attack ActionEffect from the monster so the real animation + sound play.
    private void PlaySwing()
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        var request = new ActionEffectRequest
        {
            SourceEntityId = monsterEntityId,
            SourcePosition = new Vector3(posX, posY, posZ),
            ActionId = 7, // auto-attack
            AnimationLock = 0.4f,
            SourceRotation = yaw,
            IsSourcePlayer = false,
        };
        if (player != null)
            request.Targets.Add(new TargetEffect { TargetId = player.EntityId, Damage = 0 });
        animation.PlayActionEffect(request);
    }

    private bool Down(VirtualKey k) => keyState[k];

    public void Despawn()
    {
        if (monsterAddress == nint.Zero) { pendingDraw = false; return; }

        // Stop the ragdoll referencing this address BEFORE the object is deleted.
        if (colliderRegistered)
        {
            playerRagdoll.RemoveLiveCollider(monsterAddress);
            colliderRegistered = false;
        }
        movementBlock.RemoveApproachNpc(monsterAddress);
        activeCamera.GetOrbitCenterOverride = null;
        activeCamera.SetActive(prevActiveCamState);

        // Controlled killer: it's a real enemy we don't own — release its AI, never delete it.
        if (controlledNpc != null)
        {
            controlledNpc.IsClientControlled = false;
            controlledNpc = null;
            // Reset our move-anim override only while the session is alive (avoid touching freed memory on close).
            if (Core.Services.ObjectTable.LocalPlayer != null)
                ActorVisualStateController.ClearMovement((Character*)monsterAddress, visualState);
        }
        // Spawned creature: delete the object. Only touch game memory while the session is alive —
        // during game shutdown the game has already freed it (DisableDraw/Delete would crash).
        else if (ownsObject && Core.Services.ObjectTable.LocalPlayer != null)
        {
            // Tear down the draw object/skeleton BEFORE deleting the slot (matches NpcSpawner order).
            ((GameObject*)monsterAddress)->DisableDraw();
            var mgr = ClientObjectManager.Instance();
            if (mgr != null && monsterIndex >= 0) mgr->DeleteObjectByIndex((ushort)monsterIndex, 0);
        }

        log.Info($"MonsterMode: despawned (owned={ownsObject}, idx={monsterIndex})");
        monsterIndex = -1;
        monsterAddress = nint.Zero;
        ownsObject = false;
        pendingDraw = false;
    }

    private static uint FindFreeObjectHint(ClientObjectManager* mgr)
    {
        uint hint = 100;
        while (hint < 200 && mgr->GetObjectByIndex((ushort)hint) != null)
            hint++;
        return hint;
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        Despawn();
    }
}
