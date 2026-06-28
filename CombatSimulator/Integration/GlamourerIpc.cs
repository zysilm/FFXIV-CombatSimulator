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
    /// Capture an actor's LIVE Glamourer state (including any runtime design switches the user made)
    /// as a base64 string. Returns null if Glamourer is unavailable or the call fails — callers must
    /// treat the live state as optional and fall back to the base game appearance.
    /// </summary>
    private bool loggedApiVersion;

    public string? GetStateBase64(int objectIndex)
    {
        if (objectIndex < 0) return null;

        if (!loggedApiVersion)
        {
            loggedApiVersion = true;
            try { var v = pluginInterface.GetIpcSubscriber<(int, int)>("Glamourer.ApiVersions").InvokeFunc(); log.Info($"GlamourerIpc: API version (ApiVersions) {v.Item1}.{v.Item2}"); }
            catch (Exception ex) { log.Warning($"GlamourerIpc: ApiVersions {ex.Message}"); }
            try { var v = pluginInterface.GetIpcSubscriber<int>("Glamourer.ApiVersion").InvokeFunc(); log.Info($"GlamourerIpc: API version (ApiVersion) {v}"); }
            catch (Exception ex) { log.Warning($"GlamourerIpc: ApiVersion {ex.Message}"); }
        }

        // V4 (objectIndex, key) -> (ec, base64)
        try
        {
            var s = pluginInterface.GetIpcSubscriber<int, uint, (int, string?)>("Glamourer.GetStateBase64.V4");
            var (ec, state) = s.InvokeFunc(objectIndex, 0u);
            IsAvailable = true;
            if (ec == 0 && !string.IsNullOrEmpty(state)) { log.Info($"GlamourerIpc: GetStateBase64.V4 ok len={state!.Length}"); return state; }
            log.Warning($"GlamourerIpc: GetStateBase64.V4 ec={ec} len={state?.Length ?? 0}");
        }
        catch (Exception ex) { log.Warning($"GlamourerIpc: GetStateBase64.V4 {ex.Message}"); }

        // V3/V2/V1 (objectIndex) -> (ec, base64)  [no key]
        foreach (var label in new[] { "Glamourer.GetStateBase64.V3", "Glamourer.GetStateBase64.V2", "Glamourer.GetStateBase64.V1", "Glamourer.GetStateBase64" })
        {
            try
            {
                var s = pluginInterface.GetIpcSubscriber<int, (int, string?)>(label);
                var (ec, state) = s.InvokeFunc(objectIndex);
                IsAvailable = true;
                if (ec == 0 && !string.IsNullOrEmpty(state)) { log.Info($"GlamourerIpc: {label} ok len={state!.Length}"); return state; }
                log.Warning($"GlamourerIpc: {label} ec={ec} len={state?.Length ?? 0}");
            }
            catch (Exception ex) { log.Warning($"GlamourerIpc: {label} {ex.Message}"); }
        }

        // Legacy: GetAllCustomizationFromCharacter (by object-table index) -> base64 string
        foreach (var label in new[] { "Glamourer.GetAllCustomizationFromCharacter", "Glamourer.GetAllCustomization" })
        {
            try
            {
                var s = pluginInterface.GetIpcSubscriber<int, string>(label);
                var state = s.InvokeFunc(objectIndex);
                IsAvailable = true;
                if (!string.IsNullOrEmpty(state)) { log.Info($"GlamourerIpc: {label} ok len={state.Length}"); return state; }
                log.Warning($"GlamourerIpc: {label} empty");
            }
            catch (Exception ex) { log.Warning($"GlamourerIpc: {label} {ex.Message}"); }
        }

        return null;
    }

    /// <summary>
    /// Apply a base64 state (captured via <see cref="GetStateBase64"/>) onto an actor by object-table
    /// index — used to mirror the player's live appearance onto a spawned clone. The <c>Once</c> flag is
    /// NOT set (the clone is still building its draw object). Returns false if Glamourer is unavailable.
    /// </summary>
    public bool ApplyStateBase64(string base64, int objectIndex, uint key = 0)
    {
        if (objectIndex < 0 || string.IsNullOrEmpty(base64)) return false;
        var flags = (ulong)(ApplyFlagEquipment | ApplyFlagCustomization);

        // V4 (state, objectIndex, key, flags) -> ec
        try
        {
            var s = pluginInterface.GetIpcSubscriber<object, int, uint, ulong, int>("Glamourer.ApplyState.V4");
            var r = s.InvokeFunc(base64, objectIndex, key, flags);
            IsAvailable = true;
            if (r == 0) { log.Info($"GlamourerIpc: ApplyState.V4 ok idx={objectIndex}"); return true; }
            log.Warning($"GlamourerIpc: ApplyState.V4 ec={r}");
        }
        catch (Exception ex) { log.Warning($"GlamourerIpc: ApplyState.V4 {ex.Message}"); }

        // V3 (state, objectIndex, flags) -> ec  [no key]
        try
        {
            var s = pluginInterface.GetIpcSubscriber<object, int, uint, int>("Glamourer.ApplyState.V3");
            var r = s.InvokeFunc(base64, objectIndex, (uint)flags);
            IsAvailable = true;
            if (r == 0) { log.Info($"GlamourerIpc: ApplyState.V3 ok idx={objectIndex}"); return true; }
            log.Warning($"GlamourerIpc: ApplyState.V3 ec={r}");
        }
        catch (Exception ex) { log.Warning($"GlamourerIpc: ApplyState.V3 {ex.Message}"); }

        return false;
    }

    /// <summary>
    /// Set (or strip) a single equipment/accessory slot on an actor by object-table index.
    /// Passing <paramref name="itemId"/> = 0 tells Glamourer to resolve its per-slot
    /// "Nothing" item (smallclothes for body/legs/feet/hands, empty for head/accessories) —
    /// i.e. a visual unequip.
    ///
    /// When <paramref name="persist"/> is true the <c>Once</c> flag is intentionally NOT set,
    /// so Glamourer retains the manipulation in its managed state and re-applies it on every
    /// redraw. Without this, a plain draw-object write is reverted by Glamourer on the next
    /// equipment update.
    /// </summary>
    public bool SetItem(int objectIndex, byte apiSlot, ulong itemId, bool persist = true)
    {
        if (objectIndex < 0) return false;
        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int>("Glamourer.SetItem.V3");
            // Two dye channels, both undyed. StainIds guards empty lists, but some Glamourer
            // builds expect a concrete 2-element list — pass one explicitly to be safe.
            var stains = new List<byte> { 0, 0 };
            var flags = (ulong)(persist ? ApplyFlagEquipment : ApplyFlagOnce | ApplyFlagEquipment);
            var result = subscriber.InvokeFunc(objectIndex, apiSlot, itemId, stains, 0u, flags);
            IsAvailable = true;
            if (result != 0)
                log.Warning($"GlamourerIpc: SetItem(idx={objectIndex}, slot={apiSlot}) returned ec={result}");
            return result == 0;
        }
        catch (Exception ex)
        {
            // Log full exception (incl. InnerException + stack) — the provider wraps the real
            // cause in a TargetInvocationException whose outer Message is uninformative.
            log.Error(ex, $"GlamourerIpc: SetItem(idx={objectIndex}, slot={apiSlot}) threw; inner={ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
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
