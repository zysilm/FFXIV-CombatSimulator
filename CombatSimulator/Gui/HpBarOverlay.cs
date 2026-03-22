using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CombatSimulator.Gui;

public class HpBarOverlay : IDisposable
{
    private readonly NpcSelector npcSelector;
    private readonly CombatEngine combatEngine;
    private readonly IGameGui gameGui;
    private readonly IClientState clientState;
    private readonly Configuration config;

    private const float BarWidth = 200f;
    private const float BarHeight = 16f;

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
        CombatEngine combatEngine,
        IGameGui gameGui,
        IClientState clientState,
        Configuration config)
    {
        this.npcSelector = npcSelector;
        this.combatEngine = combatEngine;
        this.gameGui = gameGui;
        this.clientState = clientState;
        this.config = config;
    }

    public unsafe void Draw()
    {
        // Hide native HP/target bars when combat sim is active
        SetNativeAddonsHidden(true);

        var drawList = ImGui.GetBackgroundDrawList();

        // Draw enemy HP bars
        if (config.ShowEnemyHpBar)
        {
            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (!npc.IsSpawned || npc.BattleChara == null)
                    continue;

                var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara;
                var worldPos = gameObj->Position;
                worldPos.Y += gameObj->Height + 0.5f;

                if (!gameGui.WorldToScreen(worldPos, out var screenPos))
                    continue;

                DrawNpcHpBar(drawList, npc, screenPos);
            }
        }

        // Draw player HP bar (world overlay, lower position near feet)
        if (config.ShowPlayerHpBar)
        {
            var player = clientState.LocalPlayer;
            if (player != null)
            {
                var worldPos = player.Position;
                worldPos.Y += 0.3f;

                if (gameGui.WorldToScreen(worldPos, out var screenPos))
                {
                    DrawPlayerHpBar(drawList, screenPos);
                }
            }
        }

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

        // Cast bar
        if (npc.State.IsCasting && npc.CurrentCastSkill != null)
        {
            var castBarY = barPos.Y + BarHeight + 2;
            float castPercent = npc.State.CastTimeTotal > 0
                ? npc.State.CastTimeElapsed / npc.State.CastTimeTotal
                : 0;

            drawList.AddRectFilled(
                new Vector2(barPos.X, castBarY),
                new Vector2(barPos.X + BarWidth, castBarY + 12),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.1f, 0.1f, 0.1f, 0.85f)));

            drawList.AddRectFilled(
                new Vector2(barPos.X, castBarY),
                new Vector2(barPos.X + BarWidth * castPercent, castBarY + 12),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.5f, 0f, 1f)));

            var castText = npc.CurrentCastSkill.Name;
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
        float hpPercent = ps.MaxHp > 0 ? (float)ps.CurrentHp / ps.MaxHp : 1;
        bool isDead = !ps.IsAlive;

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

        // Name label
        var nameText = isDead ? $"[DEAD] {ps.Name}" : $"[Sim] {ps.Name}";
        var nameColor = isDead
            ? new Vector4(1f, 0.2f, 0.2f, 1)
            : new Vector4(0.4f, 0.7f, 1f, 1);
        var nameSize = ImGui.CalcTextSize(nameText);
        drawList.AddText(
            screenPos - new Vector2(nameSize.X / 2, nameSize.Y + 4),
            ImGui.ColorConvertFloat4ToU32(nameColor),
            nameText);

        // HP text
        var hpText = isDead ? "DEFEATED" : $"{ps.CurrentHp:N0} / {ps.MaxHp:N0}";
        var hpSize = ImGui.CalcTextSize(hpText);
        drawList.AddText(
            barPos + new Vector2((BarWidth - hpSize.X) / 2, (BarHeight - hpSize.Y) / 2),
            0xFFFFFFFF,
            hpText);

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
            ImGui.SetNextWindowSize(new Vector2(BarWidth + skullSize.X + 8, BarHeight + nameSize.Y + 8));
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

    private void DrawHudPlayerHpBar()
    {
        var ps = combatEngine.State.PlayerState;
        bool isDead = !ps.IsAlive;
        float hpPercent = ps.MaxHp > 0 ? (float)ps.CurrentHp / ps.MaxHp : (isDead ? 0 : 1);

        ImGui.SetNextWindowSize(new Vector2(240, 0), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Player HP##HudPlayerHpBar", ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoFocusOnAppearing))
        {
            // Name line
            if (isDead)
            {
                ImGui.TextColored(new Vector4(1f, 0.2f, 0.2f, 1), $"[DEAD] {ps.Name}");
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
                ImGui.TextColored(new Vector4(0.4f, 0.7f, 1f, 1), $"[Sim] {ps.Name}");
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
            var hpText = isDead ? "DEFEATED" : $"{ps.CurrentHp:N0} / {ps.MaxHp:N0}";
            ImGui.ProgressBar(hpPercent, new Vector2(-1, 0), hpText);
            ImGui.PopStyleColor();
        }
        ImGui.End();
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

    public void Dispose()
    {
        RestoreNativeHpBar();
    }
}
