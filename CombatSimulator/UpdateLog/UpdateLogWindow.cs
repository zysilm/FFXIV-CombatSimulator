using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.UpdateLog;

public sealed class UpdateLogWindow
{
    private readonly Action onClose;
    private IReadOnlyList<UpdateLogEntry> entries = Array.Empty<UpdateLogEntry>();
    private bool isOpen;

    public UpdateLogWindow(Action onClose)
    {
        this.onClose = onClose;
    }

    /// <summary>Show the given entries, newest first. Caller controls ordering/count.</summary>
    public void Open(IReadOnlyList<UpdateLogEntry> entries)
    {
        this.entries = entries;
        isOpen = true;
    }

    public void Draw()
    {
        if (!isOpen || entries.Count == 0)
            return;

        ImGui.SetNextWindowSize(new Vector2(560, 480), ImGuiCond.FirstUseEver);
        var open = isOpen;
        if (!ImGui.Begin("Combat Simulator Update##CombatSimUpdateLog", ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.End();
            if (!open)
                Close();
            return;
        }

        ImGui.TextDisabled($"Most recent {entries.Count} update{(entries.Count == 1 ? "" : "s")}, newest first.");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginChild("##UpdateLogScroll", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()));
        for (var i = 0; i < entries.Count; i++)
        {
            DrawEntry(entries[i]);
            if (i < entries.Count - 1)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
            }
        }
        ImGui.EndChild();

        ImGui.Separator();
        if (ImGui.Button("Close"))
            open = false;

        ImGui.End();

        if (!open)
            Close();
    }

    private static void DrawEntry(UpdateLogEntry entry)
    {
        ImGui.Text($"Version {entry.Version}");
        if (!string.IsNullOrWhiteSpace(entry.Date))
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"({entry.Date})");
        }

        if (!string.IsNullOrWhiteSpace(entry.Title))
            ImGui.TextWrapped(entry.Title);

        ImGui.Spacing();

        foreach (var change in entry.Changes)
        {
            if (string.IsNullOrWhiteSpace(change))
                continue;
            DrawBulletWrapped(change);
        }
    }

    private void Close()
    {
        if (!isOpen)
            return;

        isOpen = false;
        onClose();
    }

    private static void DrawBulletWrapped(string text)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextWrapped(text);
    }
}
