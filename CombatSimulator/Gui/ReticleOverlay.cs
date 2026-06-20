using System;
using System.Numerics;
using CombatSimulator.ActionCombat;
using CombatSimulator.Simulation;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Gui;

/// <summary>
/// Action-Mode aim reticle: a small crosshair on the enemy the basic attack would hit right now
/// (the soft-target), so the player can read what they're locked onto without a hard target.
/// </summary>
public sealed class ReticleOverlay
{
    private const float Radius = 14f;
    private const float TickLen = 6f;
    private const float TickGap = 3f;
    private static readonly Vector3 ReticleColor = new(1.00f, 0.92f, 0.45f); // soft gold

    private readonly PlayerHitboxResolver hitbox;
    private readonly CombatEngine combatEngine;
    private readonly IGameGui gameGui;
    private readonly Configuration config;

    public ReticleOverlay(PlayerHitboxResolver hitbox, CombatEngine combatEngine, IGameGui gameGui, Configuration config)
    {
        this.hitbox = hitbox;
        this.combatEngine = combatEngine;
        this.gameGui = gameGui;
        this.config = config;
    }

    public void Draw()
    {
        if (!config.ActionMode)
            return;

        var primary = hitbox.ResolveBasicAttackPrimary();
        if (primary == null)
            return;

        var world = combatEngine.GetSimulatedEntityPosition(primary.State) + new Vector3(0f, 1.0f, 0f);
        if (!gameGui.WorldToScreen(world, out var c))
            return;

        var drawList = ImGui.GetBackgroundDrawList();
        var pulse = 1f + 0.08f * MathF.Sin((float)ImGui.GetTime() * 8f);
        var r = Radius * pulse;
        var col = Col(ReticleColor, 0.95f);
        var glow = Col(ReticleColor, 0.30f);

        drawList.AddCircle(c, r, glow, 32, 4.0f);
        drawList.AddCircle(c, r, col, 32, 1.6f);

        Span<Vector2> dirs = stackalloc Vector2[4] { new(0f, -1f), new(0f, 1f), new(-1f, 0f), new(1f, 0f) };
        foreach (var d in dirs)
            drawList.AddLine(c + d * (r + TickGap), c + d * (r + TickGap + TickLen), col, 1.8f);

        drawList.AddCircleFilled(c, 1.6f, col, 8);
    }

    private static uint Col(Vector3 rgb, float a)
        => ImGui.ColorConvertFloat4ToU32(new Vector4(rgb.X, rgb.Y, rgb.Z, Math.Clamp(a, 0f, 1f)));
}
