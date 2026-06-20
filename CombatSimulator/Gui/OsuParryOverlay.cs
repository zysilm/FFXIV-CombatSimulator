using System;
using System.Numerics;
using CombatSimulator.ActionCombat;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Gui;

/// <summary>
/// Action-Mode parry-timing UI: an osu!-style approach circle drawn on the player.
/// A steady inner "target" ring marks where to aim the timing; an outer ring starts wide
/// and faint and shrinks onto the inner ring over the enemy windup. When the two align
/// (the guard window) both rings flash — that's the moment to guard. On resolve the rings
/// briefly show the outcome: a cyan burst for a perfect guard, a red collapse on a hit,
/// a quiet fade on a dodge. Screen-space (the rings keep a clean circular shape regardless
/// of camera angle), but anchored to a world point so they track the character and scale
/// with distance. Action Mode only; nothing draws in normal mode (no telegraphs exist).
/// </summary>
public sealed class OsuParryOverlay
{
    private const int Segments = 64;
    private const float RecoveryDuration = 0.3f; // mirrors TelegraphSystem recovery

    // Colour language: calm at rest → warm as it approaches → gold-white flash at the
    // window → red on a hit → cyan on a perfect guard.
    private static readonly Vector3 RestColor = new(0.60f, 0.85f, 1.00f);
    private static readonly Vector3 ApproachColor = new(1.00f, 0.78f, 0.30f);
    private static readonly Vector3 WindowColor = new(1.00f, 0.96f, 0.78f);
    private static readonly Vector3 HitColor = new(1.00f, 0.27f, 0.20f);
    private static readonly Vector3 GuardColor = new(0.55f, 1.00f, 0.92f);

    private readonly TelegraphSystem telegraphs;
    private readonly IGameGui gameGui;
    private readonly Configuration config;

    public OsuParryOverlay(TelegraphSystem telegraphs, IGameGui gameGui, Configuration config)
    {
        this.telegraphs = telegraphs;
        this.gameGui = gameGui;
        this.config = config;
    }

    public void Draw()
    {
        if (!config.ActionMode || !config.OsuCircleEnabled)
            return;

        var list = telegraphs.Active;
        if (list.Count == 0)
            return;

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero)
            return;

        // Anchor at chest height; derive a world→pixel scale from a 0.5y reference so the
        // rings stay proportional to the character on screen at any camera distance.
        var anchorWorld = player.Position + new Vector3(0f, config.OsuAnchorHeight, 0f);
        if (!gameGui.WorldToScreen(anchorWorld, out var center))
            return;
        if (!gameGui.WorldToScreen(anchorWorld + new Vector3(0f, 0.5f, 0f), out var refUp))
            return;

        var pixelsPerYalm = MathF.Max(1f, Vector2.Distance(center, refUp) / 0.5f);
        var innerR = MathF.Max(6f, config.OsuInnerRadius * pixelsPerYalm);
        var outerStart = MathF.Max(1.2f, config.OsuOuterStartScale);

        var drawList = ImGui.GetBackgroundDrawList();
        var time = (float)ImGui.GetTime();

        foreach (var t in list)
        {
            if (!t.TargetIsPlayer)
                continue;

            if (!t.Resolved)
                DrawApproach(drawList, center, innerR, outerStart, t, time);
            else
                DrawResolved(drawList, center, innerR, t);
        }
    }

    private void DrawApproach(ImDrawListPtr drawList, Vector2 center, float innerR, float outerStart, ActiveTelegraph t, float time)
    {
        var progress = t.Progress;
        var pulse = 1f + 0.10f * MathF.Sin(time * 18f); // subtle "press now" pulse

        if (t.InGrace)
        {
            // Late-tolerance grace: rings are aligned and still pressable. Keep the gold flash and
            // let the outer ring overshoot inward toward the centre as the grace runs out.
            var graceFrac = t.GraceTotal > 0f ? Math.Clamp(t.GraceRemaining / t.GraceTotal, 0f, 1f) : 0f;
            var overshoot = innerR * (0.35f + 0.65f * graceFrac);
            DrawRingGlow(drawList, center, innerR * pulse, WindowColor, 0.95f, 2.4f);
            DrawRingGlow(drawList, center, overshoot, WindowColor, 0.9f, 2.6f);
            return;
        }

        // The guard window is the last GuardActiveWindow seconds of the windup; map it to a
        // progress band so the rings flash exactly when guarding starts to be effective.
        var windowFrac = t.WindupTotal > 0f
            ? Math.Clamp(config.GuardActiveWindow / t.WindupTotal, 0.02f, 0.9f)
            : 0.2f;
        var inWindow = progress >= 1f - windowFrac;

        // Outer ring shrinks linearly (osu-style — predictable timing) from outerStart→1.
        var outerR = innerR * (outerStart - (outerStart - 1f) * progress);
        var outerAlpha = Lerp(0.10f, 0.92f, progress);
        var approach = Vector3.Lerp(RestColor, ApproachColor, progress);

        // Inner target ring: steady and calm, brightening as the strike nears.
        var innerColor = inWindow ? WindowColor : Vector3.Lerp(RestColor, WindowColor, progress * 0.6f);
        var innerAlpha = inWindow ? 0.95f : Lerp(0.35f, 0.6f, progress);
        var pop = inWindow ? pulse : 1f;

        DrawRingGlow(drawList, center, innerR * pop, innerColor, innerAlpha, 2.2f);

        if (inWindow)
            DrawRingGlow(drawList, center, MathF.Max(outerR, innerR), WindowColor, 0.95f, 2.6f);
        else
            DrawRingGlow(drawList, center, outerR, approach, outerAlpha, 2.6f);
    }

    private void DrawResolved(ImDrawListPtr drawList, Vector2 center, float innerR, ActiveTelegraph t)
    {
        var fade = Math.Clamp(t.RecoveryRemaining / RecoveryDuration, 0f, 1f); // 1→0
        var k = 1f - fade; // 0→1 over the recovery

        switch (t.Outcome)
        {
            case TelegraphOutcome.Guarded:
                // Convergence burst: a bright ring expands outward and fades.
                var burstR = innerR * (1f + 1.4f * k);
                DrawRingGlow(drawList, center, burstR, GuardColor, fade, 3.0f);
                DrawRingGlow(drawList, center, innerR, GuardColor, fade * 0.9f, 2.2f);
                break;

            case TelegraphOutcome.Hit:
                // Red flash that collapses inward.
                var hitR = innerR * (1f - 0.4f * k);
                DrawRingGlow(drawList, center, hitR, HitColor, fade, 3.0f);
                break;

            default:
                // Dodge / no-op: quiet fade of the target ring.
                DrawRingGlow(drawList, center, innerR, RestColor, fade * 0.5f, 1.8f);
                break;
        }
    }

    // A crisp ring with a soft wider glow behind it for a bit of depth / premium feel.
    private static void DrawRingGlow(ImDrawListPtr drawList, Vector2 center, float radius, Vector3 color, float alpha, float thickness)
    {
        if (radius <= 1f || alpha <= 0.01f)
            return;

        drawList.AddCircle(center, radius, Col(color, alpha * 0.28f), Segments, thickness + 3.5f);
        drawList.AddCircle(center, radius, Col(color, alpha), Segments, thickness);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    private static uint Col(Vector3 rgb, float a)
        => ImGui.ColorConvertFloat4ToU32(new Vector4(rgb.X, rgb.Y, rgb.Z, Math.Clamp(a, 0f, 1f)));
}
