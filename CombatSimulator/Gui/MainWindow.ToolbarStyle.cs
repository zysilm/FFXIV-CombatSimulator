using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

public partial class MainWindow
{
    // Toolbars are transient controls, not full settings windows. Keep the normal font and
    // horizontal hit area, but strip most vertical chrome. A one-row toolbar is then roughly
    // 55-60% of its former height while text stays at the same readable size.
    private const int CompactToolbarStyleVarCount = 4;

    private static CompactToolbarStyleScope PushCompactToolbarStyle()
    {
        var style = ImGui.GetStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(style.WindowPadding.X, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(style.ItemSpacing.X, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(style.ItemInnerSpacing.X, 1f));
        return new CompactToolbarStyleScope();
    }

    // SetWindowFontScale is stored on the ImGui window. An earlier compact-toolbar attempt
    // wrote 0.68, so merely removing that call leaves already-created windows permanently
    // small. Explicitly restore every toolbar to the normal scale after Begin.
    private static void RestoreToolbarFontScale()
        => ImGui.SetWindowFontScale(1f);

    private readonly struct CompactToolbarStyleScope : IDisposable
    {
        public void Dispose() => ImGui.PopStyleVar(CompactToolbarStyleVarCount);
    }
}
