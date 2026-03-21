using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Integration;

public class GlamourerIpc
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    private Dictionary<Guid, string> cachedDesigns = new();

    public bool IsAvailable { get; private set; }
    public IReadOnlyDictionary<Guid, string> CachedDesigns => cachedDesigns;

    public GlamourerIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    /// <summary>
    /// Fetch all Glamourer designs. Returns empty dict if Glamourer is not installed.
    /// </summary>
    public Dictionary<Guid, string> GetDesignList()
    {
        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<Dictionary<Guid, string>>("Glamourer.GetDesignList.V2");
            cachedDesigns = subscriber.InvokeFunc();
            IsAvailable = true;
            log.Verbose($"GlamourerIpc: Fetched {cachedDesigns.Count} designs.");
            return cachedDesigns;
        }
        catch (Exception ex)
        {
            log.Warning($"GlamourerIpc: Not available ({ex.Message})");
            IsAvailable = false;
            cachedDesigns = new();
            return cachedDesigns;
        }
    }

    /// <summary>
    /// Apply a Glamourer design to the local player.
    /// flags=7 means Once|Equipment|Customization (full one-shot apply).
    /// </summary>
    public bool ApplyDesign(Guid designId)
    {
        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<Guid, int, uint, int, int>("Glamourer.ApplyDesign");
            var result = subscriber.InvokeFunc(designId, 0, 0u, 7);
            log.Info($"GlamourerIpc: ApplyDesign({designId}) = {result}");
            return result == 0;
        }
        catch (Exception ex)
        {
            log.Error(ex, "GlamourerIpc: Failed to apply design.");
            return false;
        }
    }

    /// <summary>
    /// Revert the local player's Glamourer state back to normal.
    /// </summary>
    public bool RevertState()
    {
        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<int, uint, int, int>("Glamourer.RevertState");
            var result = subscriber.InvokeFunc(0, 0u, 7);
            log.Info($"GlamourerIpc: RevertState = {result}");
            return result == 0;
        }
        catch (Exception ex)
        {
            log.Error(ex, "GlamourerIpc: Failed to revert state.");
            return false;
        }
    }
}
