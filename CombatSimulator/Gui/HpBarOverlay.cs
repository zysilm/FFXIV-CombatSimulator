using System;
using System.Numerics;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public class HpBarOverlay : IDisposable
{
    private readonly NpcSelector npcSelector;
    private readonly CombatEngine combatEngine;
    private readonly IGameGui gameGui;

    private const float BarWidth = 200f;
    private const float BarHeight = 16f;

    public HpBarOverlay(NpcSelector npcSelector, CombatEngine combatEngine, IGameGui gameGui)
    {
        this.npcSelector = npcSelector;
        this.combatEngine = combatEngine;
        this.gameGui = gameGui;
    }

    public unsafe void Draw()
    {
        var drawList = ImGui.GetBackgroundDrawList();

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

    public void Dispose()
    {
    }
}
