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

    // Glamourer ApplyFlags: Once=0x01, Equipment=0x02, Customization=0x04, Lock=0x08.
    private const int ApplyFlagOnce = 0x01;
    private const int ApplyFlagEquipment = 0x02;
    private const int ApplyFlagCustomization = 0x04;

    /// <summary>
    /// Apply a Glamourer design to a specific game object (by object-table index).
    /// When <paramref name="equipmentOnly"/> is true, only the equipment portion is
    /// applied, leaving the actor's customize bytes (race/face/hair) untouched — so
    /// companion clones keep their own/randomized appearance but wear the chosen gear.
    ///
    /// Note: the <c>Once</c> flag is intentionally NOT set. A freshly spawned companion
    /// is still building its draw object when this runs, so a one-shot apply races the
    /// redraw and gets overwritten by the cloned player gear. Without Once, Glamourer
    /// retains the equipment state and re-applies it on the actor's redraw.
    /// </summary>
    public bool ApplyDesignToObject(Guid designId, int objectIndex, bool equipmentOnly = true)
    {
        if (objectIndex < 0)
            return false;

        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<Guid, int, uint, int, int>("Glamourer.ApplyDesign");
            var flags = equipmentOnly
                ? ApplyFlagEquipment
                : ApplyFlagEquipment | ApplyFlagCustomization;
            var result = subscriber.InvokeFunc(designId, objectIndex, 0u, flags);
            log.Info($"GlamourerIpc: ApplyDesignToObject(design={designId}, idx={objectIndex}, flags={flags}) -> result={result}");
            IsAvailable = true;
            return result == 0;
        }
        catch (Exception ex)
        {
            log.Warning($"GlamourerIpc: Failed to apply design to object idx={objectIndex} ({ex.Message})");
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
