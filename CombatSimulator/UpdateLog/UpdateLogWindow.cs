using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.UpdateLog;

public sealed class UpdateLogWindow
{
    private readonly Action onClose;
    private UpdateLogEntry? entry;
    private string version = "";
    private bool isOpen;

    public UpdateLogWindow(Action onClose)
    {
        this.onClose = onClose;
    }

    public void Open(UpdateLogEntry entry, string version)
    {
        this.entry = entry;
        this.version = version;
        isOpen = true;
    }

    public void Draw()
    {
        if (!isOpen || entry == null)
            return;

        ImGui.SetNextWindowSize(new Vector2(540, 360), ImGuiCond.FirstUseEver);
        var open = isOpen;
        if (!ImGui.Begin("Combat Simulator Update##CombatSimUpdateLog", ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.End();
            if (!open)
                Close();
            return;
        }

        ImGui.Text($"Version {version}");
        if (!string.IsNullOrWhiteSpace(entry.Date))
            ImGui.TextDisabled(entry.Date);

        ImGui.Separator();
        ImGui.TextWrapped(entry.Title);
        ImGui.Spacing();

        foreach (var change in entry.Changes)
        {
            if (string.IsNullOrWhiteSpace(change))
                continue;
            DrawBulletWrapped(change);
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Close"))
            open = false;

        ImGui.End();

        if (!open)
            Close();
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
