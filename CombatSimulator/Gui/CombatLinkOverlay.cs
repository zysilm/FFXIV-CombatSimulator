using System;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using CombatSimulator.Targeting;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Gui;

/// <summary>
/// Draws the combat-link arcs and the locked-target marker (综合提升):
///  - Blue arc: player → currently locked target.
///  - Red arcs: every enemy currently attacking the player → player.
/// Arcs anchor above the head (offset so they don't cover the model), are drawn as
/// a soft semi-transparent bezier with a glow, and carry flowing particles that
/// move from the attacker toward the one being attacked. A distinct animated
/// reticle marks the locked target.
/// </summary>
public sealed unsafe class CombatLinkOverlay
{
    private readonly NpcSelector npcSelector;
    private readonly PlayerTargetController targetController;
    private readonly CombatEngine combatEngine;
    private readonly BoneTransformService boneService;
    private readonly IGameGui gameGui;
    private readonly Configuration config;

    // Player attacking → cool blue. Enemy attacking player → hot red.
    private static readonly Vector3 BlueColor = new(0.30f, 0.62f, 1.00f);
    private static readonly Vector3 RedColor = new(1.00f, 0.32f, 0.30f);

    private const int ArcSegments = 40;
    private const int FlowDots = 5;
    private const int RingSegments = 48;
    private const int RingFlowDots = 6;
    private const float RingRadius = 0.5f; // yalms

    public CombatLinkOverlay(
        NpcSelector npcSelector,
        PlayerTargetController targetController,
        CombatEngine combatEngine,
        BoneTransformService boneService,
        IGameGui gameGui,
        Configuration config)
    {
        this.npcSelector = npcSelector;
        this.targetController = targetController;
        this.combatEngine = combatEngine;
        this.boneService = boneService;
        this.gameGui = gameGui;
        this.config = config;
    }

    public void Draw()
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
            return;

        // Nothing to convey once the player is dead — drop all arcs and the marker.
        if (!combatEngine.State.PlayerState.IsAlive)
            return;

        var drawList = ImGui.GetBackgroundDrawList();
        float time = (float)ImGui.GetTime();
        var playerAnchor = HeadAnchor(player.Address, player.Position);

        if (config.ShowCombatLinkArcs)
        {
            // Blue: player → locked target.
            var locked = targetController.LockedTarget;
            if (IsDrawable(locked))
            {
                var eAnchor = HeadAnchor(locked!.Address, EnemyPos(locked));
                DrawArc(drawList, playerAnchor, eAnchor, BlueColor, time);
            }

            // Red: every enemy attacking the player → player.
            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (!npc.IsAttackingPlayer || !IsDrawable(npc))
                    continue;
                var aAnchor = HeadAnchor(npc.Address, EnemyPos(npc));
                DrawArc(drawList, aAnchor, playerAnchor, RedColor, time);
            }
        }

        if (config.ShowLockMarker)
        {
            var locked = targetController.LockedTarget;
            if (IsDrawable(locked))
                DrawFootRing(drawList, locked!, time);
        }
    }

    /// <summary>
    /// Draw a flowing arc from <paramref name="fromWorld"/> (attacker) to
    /// <paramref name="toWorld"/> (target). Flow particles travel from→to.
    /// </summary>
    private void DrawArc(ImDrawListPtr drawList, Vector3 fromWorld, Vector3 toWorld, Vector3 color, float time)
    {
        if (!gameGui.WorldToScreen(fromWorld, out var p0))
            return;
        if (!gameGui.WorldToScreen(toWorld, out var p1))
            return;

        // Control point: midpoint lifted upward (screen-space) so the link arcs over
        // the gap. Lift scales with on-screen length, clamped to stay tasteful.
        var mid = (p0 + p1) * 0.5f;
        float lift = Math.Clamp(Vector2.Distance(p0, p1) * 0.28f, 24f, 220f);
        var ctrl = mid - new Vector2(0f, lift);

        float baseAlpha = Math.Clamp(config.CombatLinkAlpha, 0.05f, 1f);
        float coreW = Math.Max(1f, config.CombatLinkThickness);

        // Glow: 3 stacked strokes, wide+faint to narrow+bright. Alpha fades toward
        // the endpoints for a softer join.
        Span<(float Width, float AlphaMul)> passes = stackalloc (float, float)[]
        {
            (coreW * 2.2f, 0.20f),
            (coreW * 1.4f, 0.45f),
            (coreW * 1.0f, 1.00f),
        };

        Vector2 prev = p0;
        for (int s = 1; s <= ArcSegments; s++)
        {
            float t = s / (float)ArcSegments;
            var cur = Bezier(p0, ctrl, p1, t);

            // Taper alpha near both ends (sin gives 0 at ends, 1 in the middle).
            float edgeFade = MathF.Sin(t * MathF.PI);
            edgeFade = 0.35f + 0.65f * edgeFade;

            foreach (var pass in passes)
            {
                uint col = Col(color, baseAlpha * pass.AlphaMul * edgeFade);
                drawList.AddLine(prev, cur, col, pass.Width);
            }
            prev = cur;
        }

        // Flowing particles moving from→to (increasing t).
        float phase = (time * Math.Max(0f, config.CombatLinkFlowSpeed)) % 1f;
        for (int i = 0; i < FlowDots; i++)
        {
            float t = ((i / (float)FlowDots) + phase) % 1f;
            var pos = Bezier(p0, ctrl, p1, t);
            // Brighten toward the head of the stream so motion direction reads clearly.
            float lead = 0.5f + 0.5f * t;
            float r = coreW * (0.9f + 0.4f * lead);
            drawList.AddCircleFilled(pos, r, Col(Vector3.Lerp(color, Vector3.One, 0.35f), Math.Min(1f, baseAlpha + 0.35f) * lead));
        }
    }

    /// <summary>
    /// A semi-transparent red ground ring at the locked target's feet, drawn in
    /// world space (perspective-correct ellipse) with the same soft-glow + flowing
    /// language as the arcs.
    /// </summary>
    private void DrawFootRing(ImDrawListPtr drawList, SimulatedNpc npc, float time)
    {
        var feet = EnemyPos(npc); // game object origin sits at the feet
        float pulse = 0.5f + 0.5f * MathF.Sin(time * 3.0f);
        float radius = RingRadius * (0.96f + 0.06f * pulse);

        // Project the ring vertices once.
        Span<Vector2> pts = stackalloc Vector2[RingSegments + 1];
        Span<bool> ok = stackalloc bool[RingSegments + 1];
        for (int i = 0; i <= RingSegments; i++)
        {
            float a = i / (float)RingSegments * MathF.Tau;
            var world = new Vector3(feet.X + MathF.Cos(a) * radius, feet.Y, feet.Z + MathF.Sin(a) * radius);
            ok[i] = gameGui.WorldToScreen(world, out pts[i]);
        }

        float baseAlpha = Math.Clamp(config.CombatLinkAlpha, 0.05f, 1f) * (0.7f + 0.3f * pulse);
        float coreW = Math.Max(1f, config.CombatLinkThickness * 0.8f);
        Span<(float Width, float AlphaMul)> passes = stackalloc (float, float)[]
        {
            (coreW * 2.4f, 0.18f),
            (coreW * 1.5f, 0.40f),
            (coreW * 1.0f, 1.00f),
        };

        for (int i = 0; i < RingSegments; i++)
        {
            if (!ok[i] || !ok[i + 1])
                continue;
            foreach (var pass in passes)
                drawList.AddLine(pts[i], pts[i + 1], Col(RedColor, baseAlpha * pass.AlphaMul), pass.Width);
        }

        // Flowing highlights sweeping around the ring (matches the arc particles).
        float phase = time * Math.Max(0f, config.CombatLinkFlowSpeed);
        for (int i = 0; i < RingFlowDots; i++)
        {
            float a = ((i / (float)RingFlowDots) + phase) % 1f * MathF.Tau;
            var world = new Vector3(feet.X + MathF.Cos(a) * radius, feet.Y, feet.Z + MathF.Sin(a) * radius);
            if (!gameGui.WorldToScreen(world, out var p))
                continue;
            drawList.AddCircleFilled(p, coreW * 1.3f, Col(Vector3.Lerp(RedColor, Vector3.One, 0.35f), Math.Min(1f, baseAlpha + 0.35f)));
        }
    }

    private static Vector2 Bezier(Vector2 p0, Vector2 c, Vector2 p1, float t)
    {
        float u = 1f - t;
        return (u * u) * p0 + (2f * u * t) * c + (t * t) * p1;
    }

    private static uint Col(Vector3 rgb, float a)
        => ImGui.ColorConvertFloat4ToU32(new Vector4(rgb.X, rgb.Y, rgb.Z, Math.Clamp(a, 0f, 1f)));

    private static bool IsDrawable(SimulatedNpc? npc)
        => npc != null && npc.IsSpawned && npc.BattleChara != null && npc.State.IsAlive;

    private static Vector3 EnemyPos(SimulatedNpc npc)
    {
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara;
        return new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
    }

    /// <summary>
    /// World anchor above the character's head: head bone position plus the
    /// configured offset, so arcs float clear of the model. Falls back to the
    /// game-object position + a fixed height when the skeleton can't be read.
    /// </summary>
    private Vector3 HeadAnchor(nint address, Vector3 fallback)
    {
        var head = GetBoneWorldPos(address, "j_kao");
        if (head != null)
        {
            var p = head.Value;
            p.Y += config.CombatLinkHeightOffset;
            return p;
        }
        fallback.Y += 2.0f + config.CombatLinkHeightOffset;
        return fallback;
    }

    private Vector3? GetBoneWorldPos(nint characterAddress, string boneName)
    {
        if (characterAddress == nint.Zero)
            return null;
        var skel = boneService.TryGetSkeleton(characterAddress);
        if (skel == null)
            return null;
        var ns = skel.Value;
        var idx = boneService.ResolveBoneIndex(ns, boneName);
        if (idx < 0 || idx >= ns.BoneCount)
            return null;
        var skeleton = ns.CharBase->Skeleton;
        if (skeleton == null)
            return null;

        var skelPos = new Vector3(
            skeleton->Transform.Position.X,
            skeleton->Transform.Position.Y,
            skeleton->Transform.Position.Z);
        var skelRot = new Quaternion(
            skeleton->Transform.Rotation.X,
            skeleton->Transform.Rotation.Y,
            skeleton->Transform.Rotation.Z,
            skeleton->Transform.Rotation.W);

        ref var mt = ref ns.Pose->ModelPose.Data[idx];
        var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
        return skelPos + Vector3.Transform(modelPos, skelRot);
    }
}
