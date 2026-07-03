using System;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Safety;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace CombatSimulator.Fighting;

/// <summary>
/// Player movement for an engaged 1v1: real fighting-game input instead of free 3D
/// movement projected onto the lane. While engaged the player's address sits in the
/// MovementBlockHook approach set (the game's own movement writes are dropped), and
/// this motor integrates a single lane coordinate from forward/back input plus a
/// self-contained parabolic jump. FightingModeController maps AlongPos/PosY back to
/// world space and writes the position.
/// </summary>
public sealed unsafe class FightingPlayerMotor
{
    private const float StickDeadzone = 0.15f;
    private const float GroundRayStart = 2f;
    private const float GroundRayDistance = 50f;

    private readonly Configuration config;
    private readonly MovementBlockHook movementBlockHook;
    private readonly IGamepadState gamepad;
    private readonly IPluginLog log;
    private readonly ActorVisualState visualState = new();

    private nint playerAddress;
    private bool began;
    private float alongPos;
    private float posY;
    private float groundY;
    private float velocityY;
    private bool airborne;
    private bool jumpKeyWasDown;
    private bool wasMoving;

    public bool IsActive => began;
    public bool IsAirborne => airborne;
    public float AlongPos => alongPos;
    public float PosY => posY;

    public FightingPlayerMotor(
        Configuration config,
        MovementBlockHook movementBlockHook,
        IGamepadState gamepad,
        IPluginLog log)
    {
        this.config = config;
        this.movementBlockHook = movementBlockHook;
        this.gamepad = gamepad;
        this.log = log;
    }

    /// <summary>Take over the player's movement. Idempotent per engage.</summary>
    public void Begin(nint address, float startAlong, float startY)
    {
        if (began && playerAddress == address)
            return;

        End();
        playerAddress = address;
        alongPos = startAlong;
        posY = startY;
        groundY = startY;
        velocityY = 0f;
        airborne = false;
        jumpKeyWasDown = true; // require a fresh press after engage
        wasMoving = false;
        began = true;
        movementBlockHook.AddApproachNpc(address);
        log.Debug($"FightingMotor: begin at along={startAlong:F2} y={startY:F2}");
    }

    /// <summary>Release the player back to normal game movement.</summary>
    public void End()
    {
        if (!began)
            return;

        if (playerAddress != nint.Zero)
        {
            movementBlockHook.RemoveApproachNpc(playerAddress);
            ClearMoveAnimation();
        }

        playerAddress = nint.Zero;
        began = false;
        airborne = false;
        velocityY = 0f;
        wasMoving = false;
    }

    /// <summary>
    /// Integrate one frame. <paramref name="enemyAlong"/> is the opponent's lane
    /// coordinate (defines which way is "forward"); <paramref name="alongToWorld"/>
    /// maps a lane coordinate to world XZ for the ground raycast.
    /// </summary>
    public void Tick(float dt, float enemyAlong, Func<float, Vector3> alongToWorld)
    {
        if (!began || playerAddress == nint.Zero)
            return;

        var axis = ReadMoveAxis();
        var jumpPressed = ReadJumpPressed();

        // Forward = toward the opponent, whichever lane direction that is.
        float forwardSign = MathF.Sign(enemyAlong - alongPos);
        if (forwardSign == 0f)
            forwardSign = 1f;

        // The side we approach from must survive the move (no walking through).
        float side = MathF.Sign(alongPos - enemyAlong);
        if (side == 0f)
            side = -forwardSign;

        if (MathF.Abs(axis) > 0.01f)
        {
            var speed = MathF.Max(0.1f, config.FightingModeMoveSpeed);
            alongPos += axis * forwardSign * speed * dt;

            var minSep = Math.Clamp(config.FightingModeMinSeparation, 0.1f, 0.65f);
            if (MathF.Abs(alongPos - enemyAlong) < minSep)
                alongPos = enemyAlong + side * minSep;
        }

        // Ground height under the (possibly moved) lane position.
        var worldXz = alongToWorld(alongPos);
        groundY = RaycastGroundY(worldXz.X, worldXz.Z, posY) ?? groundY;

        if (jumpPressed && !airborne)
        {
            velocityY = MathF.Max(0.5f, config.FightingModeJumpVelocity);
            airborne = true;
        }

        if (airborne)
        {
            posY += velocityY * dt;
            velocityY -= MathF.Max(0.1f, config.FightingModeGravity) * dt;
            if (velocityY <= 0f && posY <= groundY)
            {
                posY = groundY;
                velocityY = 0f;
                airborne = false;
            }
        }
        else
        {
            posY = groundY;
        }

        UpdateMoveAnimation(MathF.Abs(axis) > 0.01f && !airborne, dt);
    }

    private float ReadMoveAxis()
    {
        var axis = 0f;
        try
        {
            var io = ImGui.GetIO();
            if (!io.WantCaptureKeyboard && !io.WantTextInput)
            {
                var fw = GameFramework.Instance();
                if (fw != null)
                {
                    if (fw->KeyboardInputs.KeyState[config.FightingModeForwardKey].HasFlag(KeyStateFlags.Down))
                        axis += 1f;
                    if (fw->KeyboardInputs.KeyState[config.FightingModeBackKey].HasFlag(KeyStateFlags.Down))
                        axis -= 1f;
                }
            }

            // Gamepad left stick: up/forward approaches, down/back retreats.
            var stickY = gamepad.LeftStick.Y / 99f;
            if (MathF.Abs(stickY) > StickDeadzone)
                axis += stickY;
        }
        catch { }

        return Math.Clamp(axis, -1f, 1f);
    }

    private bool ReadJumpPressed()
    {
        var down = false;
        try
        {
            var io = ImGui.GetIO();
            if (!io.WantCaptureKeyboard && !io.WantTextInput)
            {
                var fw = GameFramework.Instance();
                if (fw != null)
                    down = fw->KeyboardInputs.KeyState[config.FightingModeJumpKey].HasFlag(KeyStateFlags.Down);
            }

            down |= gamepad.Raw(config.FightingModeJumpGamepadButton) != 0;
        }
        catch { }

        var pressed = down && !jumpKeyWasDown;
        jumpKeyWasDown = down;
        return pressed;
    }

    private static float? RaycastGroundY(float x, float z, float refY)
    {
        try
        {
            if (BGCollisionModule.RaycastMaterialFilter(
                    new Vector3(x, refY + GroundRayStart, z), new Vector3(0f, -1f, 0f), out var hit, GroundRayDistance))
                return hit.Point.Y;
        }
        catch { }
        return null;
    }

    private void UpdateMoveAnimation(bool moving, float dt)
    {
        try
        {
            var character = (Character*)playerAddress;
            if (character == null)
                return;

            if (moving)
            {
                ActorVisualStateController.ApplyMoving(character, visualState, dt);
                wasMoving = true;
            }
            else if (wasMoving)
            {
                ActorVisualStateController.ClearMovement(character, visualState);
                wasMoving = false;
            }
        }
        catch { }
    }

    private void ClearMoveAnimation()
    {
        if (!wasMoving)
            return;
        try
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player != null && player.Address == playerAddress)
                ActorVisualStateController.ClearMovement((Character*)playerAddress, visualState);
        }
        catch { }
        wasMoving = false;
    }
}
