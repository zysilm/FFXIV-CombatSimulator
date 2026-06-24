using System;
using System.Numerics;
using System.Text;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
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
    private readonly Configuration config;
    private readonly IPluginLog log;

    private int monsterIndex = -1;
    private uint monsterEntityId;
    private nint monsterAddress;
    private bool pendingDraw;
    private int framesWaited;
    private const int MaxPendingFrames = 60;

    private float yaw;
    private float posX, posY, posZ;
    private bool prevActiveCamState;
    private bool prevAttackKeyDown;
    private bool colliderRegistered;
    private bool cameraFollowsMonster = true;
    private readonly ActorVisualState visualState = new();

    public bool IsActive => monsterIndex >= 0;

    public MonsterModeController(IKeyState keyState, IGamepadState gamepad, IFramework framework,
        RagdollController playerRagdoll, AnimationController animation, BoneTransformService boneService,
        MovementBlockHook movementBlock, ActiveCameraController activeCamera,
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
    public bool CameraFollowsMonster => cameraFollowsMonster;

    /// <summary>Toggle the active camera between following the monster and the player (character cam).</summary>
    public void ToggleCamera()
    {
        if (!IsActive) return;
        cameraFollowsMonster = !cameraFollowsMonster;
        // Override → monster; null → active camera falls back to the player's bone.
        activeCamera.GetOrbitCenterOverride = cameraFollowsMonster ? CameraCenter : null;
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
        pendingDraw = true;
        framesWaited = 0;

        // Block the game from moving the creature so our writes win.
        movementBlock.AddApproachNpc(monsterAddress);

        // Camera follows the creature (free orbit centered on it).
        prevActiveCamState = activeCamera.IsActive;
        cameraFollowsMonster = true;
        activeCamera.GetOrbitCenterOverride = CameraCenter;
        activeCamera.SetActive(true);

        log.Info($"MonsterMode: spawned model={config.MonsterModelId} at index {index} (0x{monsterAddress:X})");
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

        // ── Vertical: keyboard Q/E + gamepad D-pad up/down. Any height, but not below ground. ──
        var up = 0f;
        if (Down(VirtualKey.Q)) up += 1f;
        if (Down(VirtualKey.E)) up -= 1f;
        if (gamepad.Raw(GamepadButtons.R2) != 0) up += 1f; // R2 ascend
        if (gamepad.Raw(GamepadButtons.L2) != 0) up -= 1f; // L2 descend
        posY += up * config.MonsterVerticalSpeed * dt;
        // No ground clamp — the creature may sink below the floor (unlimited), by request.

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

    /// <summary>Trigger the monster's attack — swing animation + sound, and punt the ragdoll.</summary>
    public void OnAttackInput()
    {
        if (!IsActive) return;

        PlaySwing();

        if (!playerRagdoll.IsActive) return;
        var monsterPos = new Vector3(posX, posY, posZ);
        // Whole-body hit: the ragdoll's body point cloud is the hit volume.
        if (playerRagdoll.PuntNearest(monsterPos, config.MonsterAttackRange, config.MonsterAttackImpulse))
            log.Info($"MonsterMode: attack impulse {config.MonsterAttackImpulse:F1}");
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
        if (monsterIndex < 0) { pendingDraw = false; return; }

        // Stop the ragdoll referencing this address BEFORE the object is deleted.
        if (colliderRegistered)
        {
            playerRagdoll.RemoveLiveCollider(monsterAddress);
            colliderRegistered = false;
        }
        movementBlock.RemoveApproachNpc(monsterAddress);
        activeCamera.GetOrbitCenterOverride = null;
        activeCamera.SetActive(prevActiveCamState);
        cameraFollowsMonster = true;

        var mgr = ClientObjectManager.Instance();
        if (mgr != null) mgr->DeleteObjectByIndex((ushort)monsterIndex, 0);
        log.Info($"MonsterMode: despawned index {monsterIndex}");
        monsterIndex = -1;
        monsterAddress = nint.Zero;
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
