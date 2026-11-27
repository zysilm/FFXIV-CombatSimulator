// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Replay;
using CombatSimulator.Replay.Model;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

/// <summary>Effects tab → "Death Replay" (docs/DEATH_REPLAY.md §7): the replay's three options and its camera shots,
/// edited with the free camera.</summary>
public partial class MainWindow
{
    /// <summary>Set by the composition root.</summary>
    internal DeathReplayDirector? DeathReplay { get; set; }

    private void DrawDeathReplaySection()
    {
        if (!ImGui.CollapsingHeader("Death Replay")) return;
        var director = DeathReplay;
        ImGui.Indent();

        var enabled = config.DeathReplayEnabled;
        if (ImGui.Checkbox("Enable##deathReplay", ref enabled))
        {
            config.DeathReplayEnabled = enabled;
            config.Save();
        }
        HelpMarker("While a simulation runs, the last 20 seconds of you and the characters near you are recorded. " +
                   "When you die, once your body has come to rest, the end of it (your fall included) is replayed " +
                   "through the camera shots below, then everything goes back to how it was.\n" +
                   "Effects, sounds and damage numbers are not replayed.");

        var seconds = Math.Clamp(config.DeathReplaySeconds, DeathReplayDirector.MinSeconds, DeathReplayDirector.MaxSeconds);
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Replay length (s)##deathReplay", ref seconds, DeathReplayDirector.MinSeconds, DeathReplayDirector.MaxSeconds, "%.0f"))
        {
            config.DeathReplaySeconds = MathF.Round(seconds);
            config.Save();
        }
        HelpMarker("How many seconds before your body came to rest are replayed.");

        var loop = config.DeathReplayLoop;
        if (ImGui.Checkbox("Loop##deathReplay", ref loop))
        {
            config.DeathReplayLoop = loop;
            config.Save();
        }
        HelpMarker("The replay repeats, the shots cycling in order, until you are revived, the simulation is reset or " +
                   "stopped, or you press Esc or Stop replay.");

        if (director != null)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"Status: {director.Status}");
            if (director.Playing)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Stop replay##deathReplay")) director.RequestStop();
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Camera shots");
        HelpMarker("The replay's time is shared equally among the shots, in this order. With no shots the camera keeps " +
                   "the view it has when the replay starts.\n" +
                   "Shots are kept relative to your body: where you stood and which way you faced when you died. While " +
                   "you are alive, they are placed relative to where you stand now, so you can set them up anywhere.");

        if (director != null)
        {
            var freeOn = director.FreeCamOn;
            if (ImGui.Button(freeOn ? "Leave free cam##deathReplay" : "Free cam##deathReplay"))
                director.RequestToggleFreeCam();
            HelpMarker("Free camera: W / S forward and back, A / D left and right, E up, Q down; hold Shift to go faster, " +
                       "Ctrl to go slower. Drag with the mouse to look round; the wheel zooms the lens. Esc leaves it.\n" +
                       "Your character stays where it is meanwhile.");
            ImGui.SameLine();
            var shotCount = config.DeathReplayShots.Count;
            ImGui.BeginDisabled(shotCount >= DeathReplayDirector.MaxShots);
            if (ImGui.Button("Add current view##deathReplay")) director.RequestAddShot();
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled($"{shotCount} / {DeathReplayDirector.MaxShots}");
            ImGui.TextDisabled(director.FrameIsDeath ? "Placed relative to your body at death." : "Placed relative to where you stand.");
        }

        DrawDeathReplayShotList(director);
        ImGui.Unindent();
    }

    private void DrawDeathReplayShotList(DeathReplayDirector? director)
    {
        var shots = config.DeathReplayShots;
        if (shots.Count == 0)
        {
            ImGui.TextDisabled("No shots yet.");
            return;
        }
        var rowHeight = ImGui.GetFrameHeightWithSpacing();
        var height = Math.Min(shots.Count, 8) * rowHeight + ImGui.GetStyle().WindowPadding.Y * 2f;
        if (!ImGui.BeginChild("##deathReplayShots", new Vector2(0, height), true))
        {
            ImGui.EndChild();
            return;
        }
        int moveUp = -1, moveDown = -1, delete = -1;
        var freeOn = director?.FreeCamOn ?? false;
        for (var i = 0; i < shots.Count; i++)
        {
            var s = shots[i];
            ImGui.PushID(i);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"Shot {i + 1}");
            ImGui.SameLine(70);
            ImGui.TextDisabled($"{s.Offset.Length():0.0} m, {s.Fov / ReplayMath.Deg:0}°");
            ImGui.SameLine(150);
            ImGui.BeginDisabled(director == null);
            if (ImGui.SmallButton("Go to")) director!.RequestGoTo(i);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(director == null || !freeOn);
            if (ImGui.SmallButton("Update")) director!.RequestUpdateShot(i);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !freeOn)
                ImGui.SetTooltip("Turn the free camera on to update a shot with its view.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete")) delete = i;
            ImGui.SameLine();
            ImGui.BeginDisabled(i == 0);
            if (ImGui.SmallButton("Up")) moveUp = i;
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(i == shots.Count - 1);
            if (ImGui.SmallButton("Down")) moveDown = i;
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.EndChild();

        if (delete >= 0)
        {
            shots.RemoveAt(delete);
            config.Save();
        }
        else if (moveUp > 0)
        {
            (shots[moveUp - 1], shots[moveUp]) = (shots[moveUp], shots[moveUp - 1]);
            config.Save();
        }
        else if (moveDown >= 0 && moveDown < shots.Count - 1)
        {
            (shots[moveDown + 1], shots[moveDown]) = (shots[moveDown], shots[moveDown + 1]);
            config.Save();
        }
    }
}
