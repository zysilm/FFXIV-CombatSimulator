using System;
using System.Numerics;
using CombatSimulator.Fighting;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Gui;

/// <summary>
/// Fighting Mode tuning overlay: draws the live main-hand weapon segment and the
/// engaged enemy's hurtbox capsule so weapon length/radius and hurtbox scale can be
/// dialed in visually. Gated on config.FightingModeDebugDraw.
/// </summary>
public sealed class FightingDebugOverlay
{
    private const int CircleSegments = 24;

    private readonly FightingModeController fighting;
    private readonly FightingCombatController fightingCombat;
    private readonly WeaponHitboxService weaponHitbox;
    private readonly IGameGui gameGui;
    private readonly Configuration config;

    public FightingDebugOverlay(
        FightingModeController fighting,
        FightingCombatController fightingCombat,
        WeaponHitboxService weaponHitbox,
        IGameGui gameGui,
        Configuration config)
    {
        this.fighting = fighting;
        this.fightingCombat = fightingCombat;
        this.weaponHitbox = weaponHitbox;
        this.gameGui = gameGui;
        this.config = config;
    }

    public void Draw()
    {
        if (!config.FightingModeDebugDraw || !fighting.IsEngaged)
            return;

        var drawList = ImGui.GetBackgroundDrawList();

        DrawWeaponSegment(drawList);
        DrawEnemyHurtbox(drawList);
    }

    private void DrawWeaponSegment(ImDrawListPtr drawList)
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero)
            return;

        // During the active window use the exact segment the sweep test used; otherwise
        // show the live resolve so the weapon line is visible while idle too.
        var seg = fightingCombat.ActiveWeaponSegment ?? weaponHitbox.GetMainHandSegment(player.Address);
        if (seg == null)
            return;

        if (!gameGui.WorldToScreen(seg.Value.Base, out var basePt) ||
            !gameGui.WorldToScreen(seg.Value.Tip, out var tipPt))
            return;

        var active = fightingCombat.ActiveWeaponSegment.HasValue;
        var color = active
            ? ImGui.GetColorU32(new Vector4(1f, 0.25f, 0.2f, 0.95f))   // red: hit window open
            : ImGui.GetColorU32(new Vector4(1f, 0.9f, 0.3f, 0.8f));    // yellow: idle
        drawList.AddLine(basePt, tipPt, color, active ? 4f : 2f);
        drawList.AddCircleFilled(tipPt, 4f, color);
    }

    private void DrawEnemyHurtbox(ImDrawListPtr drawList)
    {
        var target = fighting.CurrentTarget;
        if (target == null || target.Address == nint.Zero)
            return;

        var pos = target.GameObjectRef?.Position;
        if (pos == null)
            return;

        var radius = target.HitboxRadius * MathF.Max(0.1f, config.FightingModeHurtboxRadiusScale);
        var height = MathF.Max(0.3f, config.FightingModeHurtboxHeight);
        var color = ImGui.GetColorU32(new Vector4(0.4f, 0.95f, 1f, 0.8f));

        DrawWorldCircle(drawList, pos.Value, radius, color);
        DrawWorldCircle(drawList, pos.Value + new Vector3(0f, height, 0f), radius, color);
    }

    private void DrawWorldCircle(ImDrawListPtr drawList, Vector3 center, float radius, uint color)
    {
        Vector2? prev = null;
        Vector2? first = null;
        for (var i = 0; i <= CircleSegments; i++)
        {
            var a = i * MathF.Tau / CircleSegments;
            var world = center + new Vector3(MathF.Cos(a) * radius, 0f, MathF.Sin(a) * radius);
            if (!gameGui.WorldToScreen(world, out var screen))
            {
                prev = null;
                continue;
            }

            if (prev.HasValue)
                drawList.AddLine(prev.Value, screen, color, 1.5f);
            first ??= screen;
            prev = screen;
        }
    }
}
