using System;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Core;

public class HyperboreaDetector
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private float checkTimer;
    private const float CheckInterval = 5.0f;
    private bool wasLoaded;

    public bool IsHyperboreaLoaded { get; private set; }

    public HyperboreaDetector(IDalamudPluginInterface pluginInterface, IChatGui chatGui, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.chatGui = chatGui;
        this.log = log;

        CheckStatus();
        wasLoaded = IsHyperboreaLoaded;
    }

    public void CheckStatus()
    {
        try
        {
            IsHyperboreaLoaded = pluginInterface.InstalledPlugins
                .Any(p => p.InternalName == "Hyperborea" && p.IsLoaded);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to check Hyperborea status.");
            IsHyperboreaLoaded = false;
        }
    }

    public void Tick(float deltaTime)
    {
        checkTimer += deltaTime;
        if (checkTimer < CheckInterval)
            return;

        checkTimer = 0;
        CheckStatus();

        if (wasLoaded && !IsHyperboreaLoaded)
        {
            chatGui.PrintError("[CombatSim] WARNING: Hyperborea was unloaded! " +
                               "Combat simulation safety may be compromised.");
            log.Warning("Hyperborea was unloaded during active simulation.");
        }

        wasLoaded = IsHyperboreaLoaded;
    }
}
