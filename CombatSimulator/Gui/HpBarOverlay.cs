// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator.Gui;

public class HpBarOverlay : IDisposable
{
    private readonly NpcSelector npcSelector;
    private readonly CombatCompanionManager companionManager;
    private readonly CombatEngine combatEngine;
    private readonly BoneTransformService boneService;
    private readonly IGameGui gameGui;
    private readonly IClientState clientState;
    private readonly Configuration config;

    /// <summary>A cast that is not an engine cast (Action-Mode telegraph lead-in): name + 0..1 progress.</summary>
    public Func<SimulatedNpc, (string Name, float Progress)?>? ExternalCast { get; set; }

    private const float BarWidth = 200f;
    private const float BarHeight = 16f;
    private const float MpBarWidth = 184f;
    private const float MpBarHeight = 12f;
    private const float ResourceBarGap = 2f;

    // Everyone but the player: a small bar (smaller further off), shown only while it matters to the
    // player: the player's target, someone attacking the player, someone hit in the last few seconds,
    // anyone near, an enemy casting nearby, an ally praying / raising / held captive. The rest fade out.
    private const float SmallBarWidth = 70f;
    private const float SmallHpHeight = 5f;
    private const float SmallMpHeight = 2f;
    private const float SmallCastHeight = 4f;
    private const float SmallBarNearMeters = 10f;
    private const float SmallBarMinScale = 0.6f;
    private const float RecentHitSeconds = 5f;
    private const float NearMeters = 10f;
    private const float CastWatchMeters = 25f;
    private const float FadeSeconds = 0.25f;
    // An enemy's bar shows at any range while the camera can see it: a ray from the camera to its
    // head that hits no wall first. Each enemy is looked at again this often.
    private const float SightCheckSeconds = 0.15f;
    // With no enemy in sight in a fight: a ring under the player and an arrow on it toward the
    // nearest enemy, gone as soon as one comes into view.
    private const float CompassRadius = 1.1f;
    private const float CompassArrowLength = 0.55f;
    private const float CompassArrowWidth = 0.32f;
    private const int CompassSegments = 40;

    private readonly Dictionary<uint, (bool Seen, float At)> sight = new();
    private float compassAlpha;

    /// <summary>Whether the camera sees this point: on screen, and no wall between.</summary>
    private unsafe bool InSight(uint id, Vector3 point, Vector3 camPos, float now)
    {
        if (sight.TryGetValue(id, out var known) && now - known.At < SightCheckSeconds)
            return known.Seen;
        var seen = gameGui.WorldToScreen(point, out _);
        if (seen && camPos != Vector3.Zero)
        {
            var toPoint = point - camPos;
            var distance = toPoint.Length();
            if (distance > 0.5f &&
                BGCollisionModule.RaycastMaterialFilter(camPos, toPoint / distance, out var hit, distance) &&
                Vector3.Distance(hit.Point, camPos) < distance - 0.4f)
                seen = false;
        }
        // Spread the next looks over a few frames so twenty enemies do not all cast on one.
        sight[id] = (seen, now - (id % 5) * 0.02f);
        return seen;
    }

    /// <summary>A ring on the ground under the player and an arrow on it pointing at <paramref name="enemy"/>.</summary>
    private void DrawCompass(ImDrawListPtr drawList, Vector3 feet, Vector3 enemy, float alpha)
    {
        var ring = Fade(new Vector4(1f, 0.3f, 0.25f, 0.55f), alpha);
        var arrow = Fade(new Vector4(1f, 0.3f, 0.25f, 0.95f), alpha);
        var ground = feet + new Vector3(0f, 0.05f, 0f);
        Vector2? previous = null;
        for (var i = 0; i <= CompassSegments; i++)
        {
            var a = MathF.Tau * i / CompassSegments;
            var p = ground + new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)) * CompassRadius;
            if (gameGui.WorldToScreen(p, out var s))
            {
                if (previous is { } q)
                    drawList.AddLine(q, s, ring, 2.5f);
                previous = s;
            }
            else
                previous = null;
        }

        var dir = new Vector3(enemy.X - feet.X, 0f, enemy.Z - feet.Z);
        if (dir.LengthSquared() < 1e-4f)
            return;
        dir = Vector3.Normalize(dir);
        var side = new Vector3(dir.Z, 0f, -dir.X);
        var tip = ground + dir * (CompassRadius + CompassArrowLength);
        var left = ground + dir * CompassRadius + side * CompassArrowWidth;
        var right = ground + dir * CompassRadius - side * CompassArrowWidth;
        if (gameGui.WorldToScreen(tip, out var t) && gameGui.WorldToScreen(left, out var l) && gameGui.WorldToScreen(right, out var r))
            drawList.AddTriangleFilled(t, l, r, arrow);
    }

    /// <summary>The address of what the player has targeted (0: nothing).</summary>
    public Func<nint>? PlayerTarget { get; set; }

    private readonly Dictionary<uint, float> lastHitAt = new();
    private readonly Dictionary<uint, float> barAlpha = new();
    private readonly System.Diagnostics.Stopwatch barClock = System.Diagnostics.Stopwatch.StartNew();
    private float lastDrawAt;

    /// <summary>A hit landed on someone: their bar shows for a few seconds.</summary>
    public void NoteDamage(SimulatedEntityState target, DamageResult hit, SimulatedEntityState? source)
        => lastHitAt[target.EntityId] = (float)barClock.Elapsed.TotalSeconds;

    /// <summary>Ease this bar's opacity toward shown or hidden; the opacity now.</summary>
    private float FadeBar(uint id, bool shown, float deltaTime)
    {
        var alpha = barAlpha.GetValueOrDefault(id, 0f);
        var step = deltaTime / FadeSeconds;
        alpha = shown ? MathF.Min(1f, alpha + step) : MathF.Max(0f, alpha - step);
        barAlpha[id] = alpha;
        return alpha;
    }

    private bool HitRecently(uint id, float now)
        => lastHitAt.TryGetValue(id, out var at) && now - at <= RecentHitSeconds;

    private static float SmallBarScale(float distance)
        => Math.Clamp(SmallBarNearMeters / MathF.Max(distance, 0.01f), SmallBarMinScale, 1f);

    private static uint Fade(Vector4 color, float alpha) => ImGui.ColorConvertFloat4ToU32(color with { W = color.W * alpha });

    /// <summary>A small bar: HP, a sliver of MP, and an enemy's cast under it; an optional tag above.</summary>
    private static void DrawSmallBar(ImDrawListPtr drawList, SimulatedEntityState state, Vector2 screenPos, float scale,
        float alpha, Vector4 fill, string? tag, Vector4 tagColor, string? castName, float castProgress)
    {
        var width = SmallBarWidth * scale;
        var hpHeight = MathF.Max(3f, SmallHpHeight * scale);
        var barPos = screenPos - new Vector2(width / 2f, 0f);
        var hp = state.MaxHp > 0 ? Math.Clamp((float)Math.Max(0, state.CurrentHp) / state.MaxHp, 0f, 1f) : 0f;

        drawList.AddRectFilled(barPos, barPos + new Vector2(width, hpHeight), Fade(new Vector4(0.08f, 0.08f, 0.08f, 0.8f), alpha));
        if (hp > 0f)
            drawList.AddRectFilled(barPos, barPos + new Vector2(width * hp, hpHeight), Fade(fill, alpha));
        drawList.AddRect(barPos, barPos + new Vector2(width, hpHeight), Fade(new Vector4(0f, 0f, 0f, 0.7f), alpha));

        var y = barPos.Y + hpHeight + 1f;
        if (state.MaxMp > 0)
        {
            var mp = state.IsAlive ? Math.Clamp((float)Math.Max(0, state.CurrentMp) / state.MaxMp, 0f, 1f) : 0f;
            var mpHeight = MathF.Max(1.5f, SmallMpHeight * scale);
            drawList.AddRectFilled(new Vector2(barPos.X, y), new Vector2(barPos.X + width, y + mpHeight), Fade(new Vector4(0.04f, 0.05f, 0.1f, 0.8f), alpha));
            if (mp > 0f)
                drawList.AddRectFilled(new Vector2(barPos.X, y), new Vector2(barPos.X + width * mp, y + mpHeight), Fade(new Vector4(0.1f, 0.35f, 0.95f, 0.95f), alpha));
            y += mpHeight + 1f;
        }

        if (castName != null)
        {
            var castHeight = MathF.Max(2f, SmallCastHeight * scale);
            drawList.AddRectFilled(new Vector2(barPos.X, y), new Vector2(barPos.X + width, y + castHeight), Fade(new Vector4(0.1f, 0.1f, 0.1f, 0.85f), alpha));
            drawList.AddRectFilled(new Vector2(barPos.X, y), new Vector2(barPos.X + width * Math.Clamp(castProgress, 0f, 1f), y + castHeight), Fade(new Vector4(0.9f, 0.55f, 0f, 1f), alpha));
            var size = ImGui.CalcTextSize(castName);
            drawList.AddText(new Vector2(screenPos.X - size.X / 2f, y + castHeight + 1f), Fade(new Vector4(1f, 0.85f, 0.5f, 1f), alpha), castName);
        }

        if (tag != null)
        {
            var size = ImGui.CalcTextSize(tag);
            drawList.AddText(screenPos - new Vector2(size.X / 2f, size.Y + 2f), Fade(tagColor, alpha), tag);
        }
    }

    private static Vector4 HpFill(float hp, bool friend)
        => hp > 0.5f ? (friend ? new Vector4(0.1f, 0.6f, 0.9f, 1) : new Vector4(0.1f, 0.8f, 0.1f, 1))
            : hp > 0.25f ? new Vector4(0.8f, 0.8f, 0.1f, 1)
            : new Vector4(0.8f, 0.1f, 0.1f, 1);

    private (string Name, float Progress)? CastOf(SimulatedNpc npc)
    {
        if (npc.State.IsCasting && npc.CurrentCastSkill != null)
            return (npc.CurrentCastSkill.Name, npc.State.CastTimeTotal > 0 ? npc.State.CastTimeElapsed / npc.State.CastTimeTotal : 0f);
        return ExternalCast?.Invoke(npc);
    }

    // Native addon names to hide when combat sim is active
    private static readonly string[] NativeAddonNames =
    {
        "_ParameterWidget",        // Player HP/MP bar
        "_TargetInfo",             // Target info (combined)
        "_TargetInfoMainTarget",   // Target HP bar
        "_TargetInfoBuffDebuff",   // Target buffs/debuffs
        "_TargetInfoCastBar",      // Target cast bar
    };

    // Saved positions for hidden addons (to restore later)
    private readonly Dictionary<string, (short X, short Y)> savedAddonPositions = new();
    private bool nativeAddonsHidden;

    // Reset confirmation popup state
    private bool showResetPopup;

    public HpBarOverlay(
        NpcSelector npcSelector,
        CombatCompanionManager companionManager,
        CombatEngine combatEngine,
        BoneTransformService boneService,
        IGameGui gameGui,
        IClientState clientState,
        Configuration config)
    {
        this.npcSelector = npcSelector;
        this.companionManager = companionManager;
        this.combatEngine = combatEngine;
        this.boneService = boneService;
        this.gameGui = gameGui;
        this.clientState = clientState;
        this.config = config;
    }

    /// <summary>Names and numbers on enemies' and allies' bars: only when asked for, never in Anonymous Mode.</summary>
    private bool OthersLabelled => config.ShowAllBarLabels && !config.AnonymousMode;

    /// <summary>
    /// What a companion is doing, when it is something other than fighting (praying over the lord,
    /// raising a friend, held captive): a short tag above its bar even with names and numbers off.
    /// </summary>
    private string? SpecialStatus(SimulatedEntityState state)
    {
        var status = companionManager.StatusOf(state);
        return status is "Praying" or "Raising" or "Captive" ? status : null;
    }

    public unsafe void Draw()
    {
        // Hide native HP/target bars when combat sim is active
        SetNativeAddonsHidden(true);

        var drawList = ImGui.GetBackgroundDrawList();

        // Get camera + player bone positions for NPC HP bar occlusion
        Vector3 camPos = default;
        Vector3[]? playerBodyPoints = null;
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            var camMgr = GameCameraManager.Instance();
            if (player != null && camMgr != null && camMgr->Camera != null)
            {
                var cp = camMgr->Camera->LastPosition;
                camPos = new Vector3(cp.X, cp.Y, cp.Z);
                if (config.HpBarOcclusion)
                    playerBodyPoints = GetPlayerBodyPoints(player.Address);
            }
        }
        var anyEnemySeen = false;
        Vector3? nearestEnemy = null;
        var nearestEnemyDistance = float.MaxValue;
        var nearestEngaged = false;

        var now = (float)barClock.Elapsed.TotalSeconds;
        var deltaTime = Math.Clamp(now - lastDrawAt, 0f, 0.25f);
        lastDrawAt = now;
        var me = Core.Services.ObjectTable.LocalPlayer;
        var myPos = me?.Position ?? Vector3.Zero;
        var myTarget = PlayerTarget?.Invoke() ?? nint.Zero;
        // "Show all": every bar, full size, with names and numbers (as before).
        var full = OthersLabelled;

        // Draw enemy HP bars
        if (config.ShowEnemyHpBar)
        {
            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (!npc.IsSpawned || npc.BattleChara == null)
                    continue;

                float alpha = 1f, scale = 1f;
                (string Name, float Progress)? cast = null;
                var npcObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara;
                var npcPos = (Vector3)npcObj->Position;
                var distance = Vector3.Distance(npcPos, myPos);
                // An enemy in sight (any range, no wall between it and the camera) has its bar.
                var seen = false;
                if (npc.State.IsAlive)
                {
                    var head = GetBoneWorldPos(npc.Address, "j_kao") ?? npcPos + new Vector3(0f, MathF.Max(1f, npcObj->Height), 0f);
                    seen = InSight(npc.State.EntityId, head, camPos, now);
                    if (seen)
                        anyEnemySeen = true;
                    // The nearest enemy for the compass: one in the fight before one that has not noticed it.
                    var engaged = npc.IsEngaged;
                    if ((engaged && !nearestEngaged) || (engaged == nearestEngaged && distance < nearestEnemyDistance))
                    {
                        nearestEnemy = npcPos;
                        nearestEnemyDistance = distance;
                        nearestEngaged = engaged;
                    }
                }
                if (!full)
                {
                    cast = CastOf(npc);
                    var matters = npc.State.IsAlive && (seen || npc.Address == myTarget);
                    alpha = FadeBar(npc.State.EntityId, matters, deltaTime);
                    if (alpha <= 0f)
                        continue;
                    scale = SmallBarScale(distance);
                }

                var headPos = GetBoneWorldPos(npc.Address, "j_kao");
                Vector3 worldPos;
                if (headPos != null)
                {
                    worldPos = headPos.Value;
                    worldPos.Y += config.EnemyHpBarYOffset;
                }
                else
                {
                    var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara;
                    worldPos = new Vector3(gameObj->Position.X, gameObj->Position.Y + gameObj->Height + 0.5f, gameObj->Position.Z);
                }

                if (playerBodyPoints != null && IsOccludedByPlayer(camPos, worldPos, playerBodyPoints))
                    continue;

                if (!gameGui.WorldToScreen(worldPos, out var screenPos))
                    continue;

                if (full)
                    DrawNpcHpBar(drawList, npc, screenPos);
                else
                {
                    var hp = npc.State.MaxHp > 0 ? (float)npc.State.CurrentHp / npc.State.MaxHp : 0f;
                    DrawSmallBar(drawList, npc.State, screenPos, scale, alpha, HpFill(hp, friend: false),
                        null, default, cast?.Name, cast?.Progress ?? 0f);
                }
            }
        }

        // Draw player HP bar (world overlay — never occluded)
        if (config.ShowPlayerHpBar)
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player != null)
            {
                var headPos = GetBoneWorldPos(player.Address, "j_kao");
                Vector3 worldPos;
                if (headPos != null)
                {
                    worldPos = headPos.Value;
                    worldPos.Y += config.PlayerHpBarYOffset;
                }
                else
                {
                    worldPos = player.Position;
                    worldPos.Y += config.PlayerHpBarYOffset;
                }

                if (gameGui.WorldToScreen(worldPos, out var screenPos))
                {
                    screenPos.X += config.PlayerHpBarXOffset;
                    DrawPlayerHpBar(drawList, screenPos);
                }
            }

            foreach (var companion in companionManager.Companions)
            {
                if (!companion.IsSpawned || companion.BattleChara == null)
                    continue;

                float alpha = 1f, scale = 1f;
                string? tag = null;
                if (!full)
                {
                    var companionObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)companion.BattleChara;
                    var distance = Vector3.Distance((Vector3)companionObj->Position, myPos);
                    tag = SpecialStatus(companion.State) is { } special ? $"[{special}]" : null;
                    var matters = tag != null ||
                                  (companion.State.IsAlive &&
                                   (companion.Address == myTarget || HitRecently(companion.State.EntityId, now) || distance <= NearMeters));
                    alpha = FadeBar(companion.State.EntityId, matters, deltaTime);
                    if (alpha <= 0f)
                        continue;
                    scale = SmallBarScale(distance);
                }

                var headPos = GetBoneWorldPos(companion.Address, "j_kao");
                Vector3 worldPos;
                if (headPos != null)
                {
                    worldPos = headPos.Value;
                    worldPos.Y += config.PlayerHpBarYOffset;
                }
                else
                {
                    var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)companion.BattleChara;
                    worldPos = new Vector3(gameObj->Position.X, gameObj->Position.Y + gameObj->Height + 0.5f, gameObj->Position.Z);
                }

                if (playerBodyPoints != null && IsOccludedByPlayer(camPos, worldPos, playerBodyPoints))
                    continue;

                if (!gameGui.WorldToScreen(worldPos, out var screenPos))
                    continue;
                if (full)
                    DrawCompanionHpBar(drawList, companion, screenPos);
                else
                {
                    var state = companion.State;
                    var hp = state.MaxHp > 0 ? (float)state.CurrentHp / state.MaxHp : 0f;
                    DrawSmallBar(drawList, state, screenPos, scale, alpha,
                        state.IsAlive ? HpFill(hp, friend: true) : new Vector4(0.3f, 0f, 0f, 1),
                        tag, new Vector4(0.4f, 0.7f, 1f, 1), null, 0f);
                }
            }
        }

        // No enemy in sight during a fight: point the way to the nearest one.
        var wantCompass = combatEngine.IsActive && me != null && combatEngine.State.PlayerState.IsAlive &&
                          nearestEnemy != null && !anyEnemySeen;
        compassAlpha = wantCompass
            ? MathF.Min(1f, compassAlpha + deltaTime / FadeSeconds)
            : MathF.Max(0f, compassAlpha - deltaTime / FadeSeconds);
        if (compassAlpha > 0f && me != null && nearestEnemy is { } towards)
            DrawCompass(drawList, me.Position, towards, compassAlpha);

        // Draw HUD player HP bar (fixed screen position)
        if (config.ShowHudPlayerHpBar)
        {
            DrawHudPlayerHpBar();
        }

        // Draw reset confirmation popup (ImGui window, not background draw)
        if (showResetPopup)
            DrawResetPopup();
    }

    private void DrawNpcHpBar(ImDrawListPtr drawList, SimulatedNpc npc, Vector2 screenPos)
    {
        float hpPercent = npc.State.MaxHp > 0
            ? (float)npc.State.CurrentHp / npc.State.MaxHp
            : 0;

        var barPos = screenPos - new Vector2(BarWidth / 2, 0);

        // Background
        drawList.AddRectFilled(
            barPos,
            barPos + new Vector2(BarWidth, BarHeight),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.1f, 0.1f, 0.1f, 0.85f)));

        // HP fill
        var fillColor = hpPercent > 0.5f
            ? new Vector4(0.1f, 0.8f, 0.1f, 1)
            : hpPercent > 0.25f
                ? new Vector4(0.8f, 0.8f, 0.1f, 1)
                : new Vector4(0.8f, 0.1f, 0.1f, 1);

        if (hpPercent > 0)
        {
            drawList.AddRectFilled(
                barPos,
                barPos + new Vector2(BarWidth * hpPercent, BarHeight),
                ImGui.ColorConvertFloat4ToU32(fillColor));
        }

        // Border
        drawList.AddRect(
            barPos,
            barPos + new Vector2(BarWidth, BarHeight),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f, 0.6f, 0.6f, 0.8f)));

        if (OthersLabelled)
        {
            // Name
            var nameText = $"Lv.{npc.State.Level} {npc.Name}";
            var nameSize = ImGui.CalcTextSize(nameText);
            drawList.AddText(
                screenPos - new Vector2(nameSize.X / 2, nameSize.Y + 4),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1, 0.4f, 0.4f, 1)),
                nameText);

            // HP text
            var hpText = $"{npc.State.CurrentHp:N0} / {npc.State.MaxHp:N0}";
            var hpSize = ImGui.CalcTextSize(hpText);
            drawList.AddText(
                barPos + new Vector2((BarWidth - hpSize.X) / 2, (BarHeight - hpSize.Y) / 2),
                0xFFFFFFFF,
                hpText);
        }

        // Cast bar: the engine cast, or (Action Mode) a telegraph still in its cast lead-in.
        string? castName = null;
        float castPercent = 0f;
        if (npc.State.IsCasting && npc.CurrentCastSkill != null)
        {
            castName = npc.CurrentCastSkill.Name;
            castPercent = npc.State.CastTimeTotal > 0
                ? npc.State.CastTimeElapsed / npc.State.CastTimeTotal
                : 0;
        }
        else if (ExternalCast?.Invoke(npc) is { } external)
        {
            castName = external.Name;
            castPercent = external.Progress;
        }

        // MP under the HP, as for the player and allies.
        DrawMpBar(drawList, npc.State, barPos + new Vector2((BarWidth - MpBarWidth) / 2, BarHeight + ResourceBarGap), MpBarWidth);

        if (castName != null)
        {
            var castBarY = barPos.Y + BarHeight + ResourceBarGap + MpBarHeight + 2;

            drawList.AddRectFilled(
                new Vector2(barPos.X, castBarY),
                new Vector2(barPos.X + BarWidth, castBarY + 12),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.1f, 0.1f, 0.1f, 0.85f)));

            drawList.AddRectFilled(
                new Vector2(barPos.X, castBarY),
                new Vector2(barPos.X + BarWidth * castPercent, castBarY + 12),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.5f, 0f, 1f)));

            var castText = castName;
            var castSize = ImGui.CalcTextSize(castText);
            drawList.AddText(
                new Vector2(barPos.X + (BarWidth - castSize.X) / 2, castBarY),
                0xFFFFFFFF,
                castText);
        }
    }

    private void DrawPlayerHpBar(ImDrawListPtr drawList, Vector2 screenPos)
    {
        var ps = combatEngine.State.PlayerState;
        bool isDead = !ps.IsAlive;
        float hpPercent = ps.MaxHp > 0 ? Math.Clamp((float)Math.Max(0, ps.CurrentHp) / ps.MaxHp, 0f, 1f) : (isDead ? 0f : 1f);

        var barPos = screenPos - new Vector2(BarWidth / 2, 0);

        // Background
        drawList.AddRectFilled(
            barPos,
            barPos + new Vector2(BarWidth, BarHeight),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.1f, 0.1f, 0.1f, 0.85f)));

        // HP fill (blue tint for player to distinguish from enemy green)
        var fillColor = isDead
            ? new Vector4(0.3f, 0.0f, 0.0f, 1)
            : hpPercent > 0.5f
                ? new Vector4(0.1f, 0.6f, 0.9f, 1)
                : hpPercent > 0.25f
                    ? new Vector4(0.8f, 0.8f, 0.1f, 1)
                    : new Vector4(0.8f, 0.1f, 0.1f, 1);

        if (hpPercent > 0)
        {
            drawList.AddRectFilled(
                barPos,
                barPos + new Vector2(BarWidth * hpPercent, BarHeight),
                ImGui.ColorConvertFloat4ToU32(fillColor));
        }

        // Border
        drawList.AddRect(
            barPos,
            barPos + new Vector2(BarWidth, BarHeight),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f, 0.6f, 0.6f, 0.8f)));

        var nameSize = Vector2.Zero;
        if (!config.AnonymousMode)
        {
            // Name label
            var displayName = !string.IsNullOrEmpty(config.CustomPlayerName) ? config.CustomPlayerName : ps.Name;
            var nameText = BuildPlayerNameLabel(isDead, displayName);
            var nameColor = isDead
                ? new Vector4(1f, 0.2f, 0.2f, 1)
                : new Vector4(0.4f, 0.7f, 1f, 1);
            nameSize = ImGui.CalcTextSize(nameText);
            drawList.AddText(
                screenPos - new Vector2(nameSize.X / 2, nameSize.Y + 4),
                ImGui.ColorConvertFloat4ToU32(nameColor),
                nameText);
        }

        // HP text (Anonymous Mode hides every number)
        if (!config.AnonymousMode)
        {
            var hpText = $"{ps.CurrentHp:N0} / {ps.MaxHp:N0}";
            var hpSize = ImGui.CalcTextSize(hpText);
            drawList.AddText(
                barPos + new Vector2((BarWidth - hpSize.X) / 2, (BarHeight - hpSize.Y) / 2),
                0xFFFFFFFF,
                hpText);
        }

        DrawMpBar(drawList, ps, barPos + new Vector2((BarWidth - MpBarWidth) / 2, BarHeight + ResourceBarGap), MpBarWidth);

        if (config.AnonymousMode)
            return;

        // Skull button when dead — drawn as an invisible ImGui button over the bar area
        if (isDead)
        {
            // Draw skull symbol to the right of the bar
            var skullText = "\u2620"; // Unicode skull and crossbones
            var skullSize = ImGui.CalcTextSize(skullText);
            var skullPos = new Vector2(barPos.X + BarWidth + 4, barPos.Y + (BarHeight - skullSize.Y) / 2);

            // Pulsing red color for attention
            float pulse = (MathF.Sin((float)ImGui.GetTime() * 4f) + 1f) / 2f;
            var skullColor = new Vector4(1f, 0.1f + pulse * 0.3f, 0.1f, 1f);
            drawList.AddText(skullPos, ImGui.ColorConvertFloat4ToU32(skullColor), skullText);

            // Create an invisible button over the skull + bar area for click detection
            // We need to use an ImGui window for this since background drawlist doesn't handle input
            ImGui.SetNextWindowPos(barPos - new Vector2(0, nameSize.Y + 4), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(BarWidth + skullSize.X + 8, BarHeight + MpBarHeight + ResourceBarGap + nameSize.Y + 8));
            if (ImGui.Begin("##DeathClickArea", ImGuiWindowFlags.NoDecoration |
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoBringToFrontOnFocus))
            {
                if (ImGui.InvisibleButton("##SkullReset", ImGui.GetContentRegionAvail()))
                {
                    showResetPopup = true;
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("You are defeated! Click to reset battle.");
                }
                ImGui.End();
            }
        }
    }

    private void DrawCompanionHpBar(ImDrawListPtr drawList, CombatCompanion companion, Vector2 screenPos)
    {
        var state = companion.State;
        var isDead = !state.IsAlive;
        var hpPercent = state.MaxHp > 0 ? Math.Clamp((float)Math.Max(0, state.CurrentHp) / state.MaxHp, 0f, 1f) : 0;
        var barPos = screenPos - new Vector2(BarWidth / 2, 0);

        drawList.AddRectFilled(
            barPos,
            barPos + new Vector2(BarWidth, BarHeight),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.1f, 0.1f, 0.1f, 0.85f)));

        var fillColor = isDead
            ? new Vector4(0.3f, 0f, 0f, 1)
            : hpPercent > 0.5f
                ? new Vector4(0.1f, 0.6f, 0.9f, 1)
                : hpPercent > 0.25f
                    ? new Vector4(0.8f, 0.8f, 0.1f, 1)
                    : new Vector4(0.8f, 0.1f, 0.1f, 1);

        if (hpPercent > 0)
        {
            drawList.AddRectFilled(
                barPos,
                barPos + new Vector2(BarWidth * hpPercent, BarHeight),
                ImGui.ColorConvertFloat4ToU32(fillColor));
        }

        drawList.AddRect(
            barPos,
            barPos + new Vector2(BarWidth, BarHeight),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f, 0.6f, 0.6f, 0.8f)));

        // Name and numbers only when asked for; otherwise just what it is doing when that matters.
        var label = OthersLabelled
            ? $"[{companionManager.StatusOf(state)}] Lv.{state.Level} {state.Name}"
            : SpecialStatus(state) is { } special ? $"[{special}]" : null;
        if (label != null)
        {
            var nameSize = ImGui.CalcTextSize(label);
            drawList.AddText(
                screenPos - new Vector2(nameSize.X / 2, nameSize.Y + 4),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.4f, 0.7f, 1f, 1)),
                label);
        }

        if (OthersLabelled)
        {
            var hpText = $"{state.CurrentHp:N0} / {state.MaxHp:N0}";
            var hpSize = ImGui.CalcTextSize(hpText);
            drawList.AddText(
                barPos + new Vector2((BarWidth - hpSize.X) / 2, (BarHeight - hpSize.Y) / 2),
                0xFFFFFFFF,
                hpText);
        }

        DrawMpBar(drawList, state, barPos + new Vector2((BarWidth - MpBarWidth) / 2, BarHeight + ResourceBarGap), MpBarWidth);
    }

    private void DrawHudPlayerHpBar()
    {
        var ps = combatEngine.State.PlayerState;
        bool isDead = !ps.IsAlive;
        float hpPercent = ps.MaxHp > 0 ? Math.Clamp((float)Math.Max(0, ps.CurrentHp) / ps.MaxHp, 0f, 1f) : (isDead ? 0 : 1);
        var displayedMp = isDead ? 0 : Math.Max(0, ps.CurrentMp);
        float mpPercent = ps.MaxMp > 0 ? Math.Clamp((float)displayedMp / ps.MaxMp, 0f, 1f) : 0f;

        ImGui.SetNextWindowSize(new Vector2(240, 0), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Player HP##HudPlayerHpBar", ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoFocusOnAppearing))
        {
            if (!config.AnonymousMode)
            {
                // Name line
                var hudDisplayName = !string.IsNullOrEmpty(config.CustomPlayerName) ? config.CustomPlayerName : ps.Name;
                if (isDead)
                {
                    ImGui.TextColored(new Vector4(1f, 0.2f, 0.2f, 1), BuildPlayerNameLabel(true, hudDisplayName));
                    ImGui.SameLine();

                    float pulse = (MathF.Sin((float)ImGui.GetTime() * 4f) + 1f) / 2f;
                    var skullColor = new Vector4(1f, 0.1f + pulse * 0.3f, 0.1f, 1f);
                    ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 0.3f, 0.3f, 0.3f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 0.3f, 0.3f, 0.5f));
                    ImGui.PushStyleColor(ImGuiCol.Text, skullColor);
                    if (ImGui.SmallButton("\u2620##hudReset"))
                    {
                        showResetPopup = true;
                    }
                    ImGui.PopStyleColor(4);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Click to reset battle");
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.4f, 0.7f, 1f, 1), BuildPlayerNameLabel(false, hudDisplayName));
                }
            }

            // HP bar
            var fillColor = isDead
                ? new Vector4(0.3f, 0f, 0f, 1)
                : hpPercent > 0.5f
                    ? new Vector4(0.1f, 0.6f, 0.9f, 1)
                    : hpPercent > 0.25f
                        ? new Vector4(0.8f, 0.8f, 0.1f, 1)
                        : new Vector4(0.8f, 0.1f, 0.1f, 1);

            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, fillColor);
            var hpText = config.AnonymousMode ? "" : $"{ps.CurrentHp:N0} / {ps.MaxHp:N0}";
            ImGui.ProgressBar(hpPercent, new Vector2(-1, 0), hpText);
            ImGui.PopStyleColor();

            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, new Vector4(0.1f, 0.35f, 0.95f, 1f));
            ImGui.ProgressBar(mpPercent, new Vector2(-1, 0), config.AnonymousMode ? "" : $"{displayedMp:N0} / {ps.MaxMp:N0} MP");
            ImGui.PopStyleColor();
        }
        ImGui.End();
    }

    private void DrawMpBar(ImDrawListPtr drawList, SimulatedEntityState state, Vector2 barPos, float width)
    {
        if (state.MaxMp <= 0)
            return;

        var displayedMp = state.IsAlive ? Math.Max(0, state.CurrentMp) : 0;
        var mpPercent = Math.Clamp((float)displayedMp / state.MaxMp, 0f, 1f);
        var bgColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.04f, 0.05f, 0.10f, 0.88f));
        var fillColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.1f, 0.35f, 0.95f, 0.95f));
        var borderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.35f, 0.75f, 0.8f));

        drawList.AddRectFilled(
            barPos,
            barPos + new Vector2(width, MpBarHeight),
            bgColor);

        if (mpPercent > 0f)
        {
            drawList.AddRectFilled(
                barPos,
                barPos + new Vector2(width * mpPercent, MpBarHeight),
                fillColor);
        }

        drawList.AddRect(
            barPos,
            barPos + new Vector2(width, MpBarHeight),
            borderColor);
    }

    /// <summary>The player's name with what they are doing in front: Fighting, Captive or Fallen.</summary>
    private string BuildPlayerNameLabel(bool isDead, string displayName)
    {
        var resolvedName = displayName;
        if (isDead && !string.IsNullOrWhiteSpace(config.DeathNameAlias))
            resolvedName = config.DeathNameAlias.Trim();

        return $"[{companionManager.StatusOf(combatEngine.State.PlayerState)}] {resolvedName}";
    }

    private void DrawResetPopup()
    {
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing,
            new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(300, 0));

        if (ImGui.Begin("Defeated!", ref showResetPopup,
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("You have been defeated in combat!");
            ImGui.Spacing();
            ImGui.TextWrapped("Would you like to reset the battle?");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            var buttonWidth = 120f;
            var spacing = ImGui.GetContentRegionAvail().X - buttonWidth * 2;

            if (ImGui.Button("Reset Battle", new Vector2(buttonWidth, 0)))
            {
                combatEngine.ResetState();
                showResetPopup = false;
            }

            ImGui.SameLine(0, spacing);

            if (ImGui.Button("Cancel", new Vector2(buttonWidth, 0)))
            {
                showResetPopup = false;
            }

            ImGui.End();
        }
    }

    /// <summary>
    /// Restore native UI addons. Called when sim stops or plugin disposes.
    /// </summary>
    public void RestoreNativeHpBar()
    {
        if (nativeAddonsHidden)
            SetNativeAddonsHidden(false);
    }

    /// <summary>
    /// Hide native addons by moving them off-screen (robust against game re-enabling IsVisible).
    /// Restore by moving them back to saved positions.
    /// </summary>
    private unsafe void SetNativeAddonsHidden(bool hide)
    {
        try
        {
            var stage = AtkStage.Instance();
            if (stage == null || stage->RaptureAtkUnitManager == null)
                return;

            foreach (var name in NativeAddonNames)
            {
                var addon = stage->RaptureAtkUnitManager->GetAddonByName(name);
                if (addon == null)
                    continue;

                if (hide)
                {
                    // Only save position if we haven't already hidden it
                    if (!savedAddonPositions.ContainsKey(name))
                        savedAddonPositions[name] = (addon->X, addon->Y);

                    addon->SetPosition(-9999, -9999);
                }
                else
                {
                    if (savedAddonPositions.TryGetValue(name, out var pos))
                    {
                        addon->SetPosition(pos.X, pos.Y);
                    }
                }
            }

            if (hide)
                nativeAddonsHidden = true;
            else
            {
                nativeAddonsHidden = false;
                savedAddonPositions.Clear();
            }
        }
        catch
        {
            // Silently ignore — addons may not exist in all contexts
        }
    }

    // Bones to sample for player body occlusion (spread across the skeleton)
    private static readonly string[] OcclusionBones = {
        "j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_kubi", "j_kao",
        "j_asi_a_l", "j_asi_a_r", "j_asi_b_l", "j_asi_b_r",
        "j_ude_a_l", "j_ude_a_r",
    };

    private unsafe Vector3[] GetPlayerBodyPoints(nint playerAddress)
    {
        var points = new List<Vector3>();
        var skel = boneService.TryGetSkeleton(playerAddress);
        if (skel == null) return points.ToArray();
        var ns = skel.Value;
        var skeleton = ns.CharBase->Skeleton;
        if (skeleton == null) return points.ToArray();

        var skelPos = new Vector3(
            skeleton->Transform.Position.X,
            skeleton->Transform.Position.Y,
            skeleton->Transform.Position.Z);
        var skelRot = new Quaternion(
            skeleton->Transform.Rotation.X,
            skeleton->Transform.Rotation.Y,
            skeleton->Transform.Rotation.Z,
            skeleton->Transform.Rotation.W);

        foreach (var boneName in OcclusionBones)
        {
            var idx = boneService.ResolveBoneIndex(ns, boneName);
            if (idx < 0 || idx >= ns.BoneCount) continue;
            ref var mt = ref ns.Pose->ModelPose.Data[idx];
            var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            points.Add(skelPos + Vector3.Transform(modelPos, skelRot));
        }
        return points.ToArray();
    }

    /// <summary>
    /// Check if any of the player's bone positions are between the camera and the HP bar.
    /// </summary>
    private static bool IsOccludedByPlayer(Vector3 camPos, Vector3 hpBarWorldPos, Vector3[] bodyPoints)
    {
        var camToBar = hpBarWorldPos - camPos;
        var lineLen = camToBar.Length();
        if (lineLen < 0.01f) return false;
        var lineDir = camToBar / lineLen;

        foreach (var bodyPoint in bodyPoints)
        {
            var camToBody = bodyPoint - camPos;
            var t = Vector3.Dot(camToBody, lineDir);
            if (t < 0f || t > lineLen) continue;

            var closest = camPos + lineDir * t;
            var dist = Vector3.Distance(bodyPoint, closest);
            if (dist < 0.2f) return true;
        }
        return false;
    }

    private unsafe Vector3? GetBoneWorldPos(nint characterAddress, string boneName)
    {
        if (characterAddress == nint.Zero) return null;
        var skel = boneService.TryGetSkeleton(characterAddress);
        if (skel == null) return null;
        var ns = skel.Value;
        var idx = boneService.ResolveBoneIndex(ns, boneName);
        if (idx < 0 || idx >= ns.BoneCount) return null;
        var skeleton = ns.CharBase->Skeleton;
        if (skeleton == null) return null;

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

    public void Dispose()
    {
        RestoreNativeHpBar();
    }
}
