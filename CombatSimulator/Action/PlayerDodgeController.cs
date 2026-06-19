using System;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Safety;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.ActionCombat;

/// <summary>
/// Action Mode dodge: a short backstep dash with an invulnerability window. During
/// the dash the player object is moved through <see cref="MovementBlockHook"/> (the
/// same bypass used for NPC approach), and the player's own movement writes are
/// blocked so the slide is clean. Telegraph hits that land while
/// <see cref="IsInvulnerable"/> is true are nullified.
///
/// NOTE: no dedicated dodge animation yet — FFXIV has no clean roll. The dash reads
/// as a quick slide for now; an animation pass is a follow-up (see plan Risks).
/// </summary>
public sealed unsafe class PlayerDodgeController
{
    private readonly MovementBlockHook movementBlock;
    private readonly IPluginLog log;

    private readonly Configuration config;

    private float invulnTimer;
    private float dashTimer;
    private float cooldownTimer;
    private Vector3 dashDir;
    private nint dashAddress;

    public bool IsInvulnerable => invulnTimer > 0f;

    public PlayerDodgeController(MovementBlockHook movementBlock, Configuration config, IPluginLog log)
    {
        this.movementBlock = movementBlock;
        this.config = config;
        this.log = log;
    }

    public void TryDodge()
    {
        if (cooldownTimer > 0f || dashTimer > 0f)
            return;

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero)
            return;

        // Backstep: dash away from facing. Tunable; a movement-input direction is a
        // future refinement.
        var rot = player.Rotation;
        dashDir = new Vector3(-MathF.Sin(rot), 0f, -MathF.Cos(rot));
        dashTimer = config.DodgeDuration;
        invulnTimer = config.DodgeIFrames;
        cooldownTimer = config.DodgeCooldown;
        dashAddress = player.Address;
        movementBlock.AddApproachNpc(dashAddress); // block the game's own player writes during the dash
    }

    public void Tick(float dt)
    {
        if (invulnTimer > 0f) invulnTimer = MathF.Max(0f, invulnTimer - dt);
        if (cooldownTimer > 0f) cooldownTimer = MathF.Max(0f, cooldownTimer - dt);

        if (dashTimer > 0f)
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player != null && player.Address == dashAddress && dashAddress != nint.Zero)
            {
                var obj = (GameObject*)dashAddress;
                var cur = (Vector3)obj->Position;
                var speed = config.DodgeDistance / MathF.Max(0.05f, config.DodgeDuration);
                var next = cur + dashDir * speed * dt;
                next.Y = cur.Y;
                movementBlock.SetApproachPosition(obj, next.X, next.Y, next.Z);
            }

            dashTimer -= dt;
            if (dashTimer <= 0f)
                EndDash();
        }
    }

    private void EndDash()
    {
        if (dashAddress != nint.Zero)
            movementBlock.RemoveApproachNpc(dashAddress);
        dashAddress = nint.Zero;
    }

    public void Reset()
    {
        EndDash();
        invulnTimer = 0f;
        dashTimer = 0f;
        cooldownTimer = 0f;
    }
}
