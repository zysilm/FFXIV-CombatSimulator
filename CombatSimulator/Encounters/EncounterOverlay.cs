// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using CombatSimulator.Encounters.Runtime;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Encounters;

public sealed class EncounterOverlay
{
    private readonly EncounterDirector director;
    private readonly StoryEncounterPromptController storyPrompt;

    public EncounterOverlay(EncounterDirector director, StoryEncounterPromptController storyPrompt)
    {
        this.director = director;
        this.storyPrompt = storyPrompt;
    }

    public void Draw()
    {
        DrawPresentation();
        DrawStoryPrompt();
    }

    private void DrawPresentation()
    {
        if (!director.HasPresentation) return;

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            viewport.WorkPos + new Vector2(viewport.WorkSize.X * 0.5f, viewport.WorkSize.Y * 0.16f),
            ImGuiCond.Always,
            new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.78f);
        ImGui.SetNextWindowSizeConstraints(new Vector2(360f, 0f), new Vector2(760f, 220f));

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration |
                                       ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoSavedSettings |
                                       ImGuiWindowFlags.NoFocusOnAppearing |
                                       ImGuiWindowFlags.NoNav |
                                       ImGuiWindowFlags.NoInputs;
        if (!ImGui.Begin("##EncounterPresentation", flags))
        {
            ImGui.End();
            return;
        }

        if (!string.IsNullOrWhiteSpace(director.PresentationTitle))
        {
            var size = ImGui.CalcTextSize(director.PresentationTitle);
            ImGui.SetCursorPosX((ImGui.GetWindowWidth() - size.X) * 0.5f);
            ImGui.TextColored(new Vector4(1f, 0.78f, 0.25f, 1f), director.PresentationTitle);
        }
        if (!string.IsNullOrWhiteSpace(director.PresentationSpeaker))
            ImGui.TextColored(new Vector4(1f, 0.78f, 0.25f, 1f), director.PresentationSpeaker);
        if (!string.IsNullOrWhiteSpace(director.PresentationText))
            ImGui.TextWrapped(director.PresentationText);
        ImGui.End();
    }

    private void DrawStoryPrompt()
    {
        var encounter = storyPrompt.PendingEncounter;
        if (!storyPrompt.IsPromptVisible || encounter == null) return;

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            viewport.WorkPos + viewport.WorkSize * 0.5f,
            ImGuiCond.Always,
            new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(430f, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.94f);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.NoSavedSettings |
                                       ImGuiWindowFlags.AlwaysAutoResize;
        if (!ImGui.Begin("Continue as a local encounter?##StoryEncounterPrompt", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("The story scene has ended. Start an optional client-side Echo battle?");
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.78f, 0.25f, 1f), encounter.Name);
        ImGui.TextWrapped(encounter.Description);
        ImGui.Spacing();
        ImGui.TextDisabled("This does not advance or alter the quest.");
        ImGui.Spacing();

        if (ImGui.Button("Enter Battle", new Vector2(130f, 0f)))
            storyPrompt.Accept();
        ImGui.SameLine();
        if (ImGui.Button("Not Now", new Vector2(100f, 0f)))
            storyPrompt.Decline();
        ImGui.End();
    }
}
