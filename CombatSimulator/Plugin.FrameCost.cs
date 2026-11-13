// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator;

/// <summary>
/// A summary, every few seconds while corpses are about, of where the frame time of ragdolls and
/// corpse collision goes. Written to the plugin log only (no UI): it is what tells a crowd's slowdown
/// apart — tracking living characters, stepping physics, or placing feet on corpses.
/// </summary>
public sealed partial class CombatSimulatorPlugin
{
    private const float FrameCostReportSeconds = 5f;
    private float frameCostWindow;
    private int frameCostGameFrames;
    private float frameCostSlowestFrame;

    private void OnFrameworkUpdateTimed(Dalamud.Plugin.Services.IFramework fw)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        OnFrameworkUpdate(fw);
        Core.FrameCostMeter.Add("Plugin.FrameworkUpdate", System.Diagnostics.Stopwatch.GetTimestamp() - start);
    }

    private void NoteFrameCost(float frameSeconds)
    {
        frameCostWindow += frameSeconds;
        frameCostGameFrames++;
        if (frameSeconds > frameCostSlowestFrame)
            frameCostSlowestFrame = frameSeconds;
        if (frameCostWindow < FrameCostReportSeconds)
            return;

        var ragdoll = Animation.RagdollController.TakeFrameCost();
        var feetMs = Npcs.NpcCorpseFootIkSolver.TakeCostMs();
        var frames = System.Math.Max(1, frameCostGameFrames);
        if (ragdoll.Frames > 0 || feetMs > 0)
        {
            log.Info(
                $"FrameCost: {frames / frameCostWindow:F0} fps (slowest {frameCostSlowestFrame * 1000f:F0} ms), " +
                $"{npcRagdolls.Count} corpses, {npcSelector.SelectedNpcs.Count} enemies, {companionManager.Companions.Count} allies, " +
                $"NPC collision {(config.NpcCollisionActive ? "on" : "off")} | per game frame: " +
                $"ragdolls {ragdoll.TotalMs / frames:F2} ms (tracking characters {ragdoll.CollidersMs / frames:F2}, " +
                $"physics {ragdoll.PhysicsMs / frames:F2}), feet on corpses {feetMs / frames:F2} ms, " +
                $"{(double)ragdoll.SteppedFrames / frames:F1} ragdolls stepping, " +
                $"{(double)ragdoll.TrackedColliders / frames:F0} character collision sets tracked | costliest per frame (ms): " +
                Core.FrameCostMeter.TakeTop(8, frames));
        }
        else
            Core.FrameCostMeter.TakeTop(0, frames);

        frameCostWindow = 0f;
        frameCostGameFrames = 0;
        frameCostSlowestFrame = 0f;
    }
}
