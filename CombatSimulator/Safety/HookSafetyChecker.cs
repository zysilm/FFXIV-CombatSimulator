using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Safety;

/// <summary>
/// Detects whether native game functions are hooked by other plugins
/// by inspecting the first bytes at each function address for JMP detours.
/// Resolves the hooking plugin via Dalamud's internal HookManager.TrackedHooks,
/// which maps every hook to the Assembly that created it.
/// </summary>
public class HookSafetyChecker
{
    private readonly IPluginLog log;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly List<CheckedFunction> functions = new();

    public IReadOnlyList<CheckedFunction> Functions => functions;
    public bool HasConflicts { get; private set; }

    public class CheckedFunction
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public nint Address { get; init; }
        public byte ExpectedFirstByte { get; init; }
        public bool IsHooked { get; set; }
        public string DetourInfo { get; set; } = "";
        public string HookedBy { get; set; } = "";
    }

    public HookSafetyChecker(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public void Register(string name, string description, nint address, byte expectedFirstByte = 0)
    {
        if (address == 0) return;
        functions.Add(new CheckedFunction
        {
            Name = name,
            Description = description,
            Address = address,
            ExpectedFirstByte = expectedFirstByte,
        });
    }

    public void Scan()
    {
        // Query Dalamud's internal hook registry: address → list of owning assembly names
        var hookOwners = QueryDalamudTrackedHooks();
        var loadedPlugins = GetLoadedPluginNames();

        HasConflicts = false;
        foreach (var func in functions)
        {
            func.IsHooked = false;
            func.DetourInfo = "";
            func.HookedBy = "";

            try
            {
                var b0 = Marshal.ReadByte(func.Address);
                var b1 = Marshal.ReadByte(func.Address + 1);

                // Detect JMP detour patterns
                bool jmpDetected = false;
                if (b0 == 0xE9 || (b0 == 0xFF && b1 == 0x25) || (b0 == 0x48 && b1 == 0xB8) ||
                    (b0 == 0x49 && b1 == 0xBB) || b0 == 0xEB ||
                    (b0 == 0x48 && b1 == 0xFF && Marshal.ReadByte(func.Address + 2) == 0x25))
                {
                    jmpDetected = true;
                }

                // Fallback: expected first byte mismatch
                if (!jmpDetected && func.ExpectedFirstByte != 0 && b0 != func.ExpectedFirstByte)
                {
                    jmpDetected = true;
                    func.DetourInfo = $"Patched (0x{b0:X2} != expected 0x{func.ExpectedFirstByte:X2})";
                }

                // Determine hooked status + identify hooker
                bool inTrackedHooks = hookOwners.TryGetValue(func.Address, out var owners) && owners.Count > 0;
                if (inTrackedHooks || jmpDetected)
                {
                    func.IsHooked = true;
                    if (inTrackedHooks)
                    {
                        func.HookedBy = string.Join(", ", owners!);
                    }
                    else
                    {
                        // Bytes patched but no active hook registered — residual from a plugin
                        // that unloaded without restoring original bytes, or a non-Dalamud hook
                        func.HookedBy = "(residual — restart game to clear)";
                    }
                    if (string.IsNullOrEmpty(func.DetourInfo))
                        func.DetourInfo = inTrackedHooks ? "Dalamud HookManager" : $"Patched bytes (0x{b0:X2} {b1:X2})";
                }

                if (func.IsHooked)
                {
                    HasConflicts = true;
                    var by = string.IsNullOrEmpty(func.HookedBy) ? "" : $" by {func.HookedBy}";
                    log.Warning($"[HookSafety] {func.Name} HOOKED{by} ({func.DetourInfo})");
                }
                else
                {
                    log.Info($"[HookSafety] {func.Name} — clean (0x{b0:X2} {b1:X2})");
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"[HookSafety] Failed to scan {func.Name}");
            }
        }

        log.Info($"[HookSafety] Scan complete: {(HasConflicts ? "CONFLICTS FOUND" : "CLEAN")}");
    }

    /// <summary>
    /// Access Dalamud's internal HookManager.TrackedHooks via reflection.
    /// Returns a map of hooked address → list of plugin/assembly names that hook it.
    /// </summary>
    private Dictionary<nint, List<string>> QueryDalamudTrackedHooks()
    {
        var result = new Dictionary<nint, List<string>>();

        try
        {
            // Dalamud.Hooking.Internal.HookManager is internal — access via reflection
            var dalamudAssembly = typeof(IDalamudPluginInterface).Assembly;
            var hookManagerType = dalamudAssembly.GetType("Dalamud.Hooking.Internal.HookManager");
            if (hookManagerType == null)
            {
                log.Warning("[HookSafety] Could not find HookManager type via reflection.");
                return result;
            }

            // HookManager.TrackedHooks is a static ConcurrentDictionary<Guid, HookInfo>
            var trackedHooksProp = hookManagerType.GetProperty("TrackedHooks",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (trackedHooksProp == null)
            {
                log.Warning("[HookSafety] Could not find TrackedHooks property.");
                return result;
            }

            var trackedHooks = trackedHooksProp.GetValue(null);
            if (trackedHooks == null)
            {
                log.Warning("[HookSafety] TrackedHooks is null.");
                return result;
            }

            // HookInfo has: Hook (IDalamudHook with .Address), Assembly
            var hookInfoType = dalamudAssembly.GetType("Dalamud.Hooking.Internal.HookInfo");
            var hookProp = hookInfoType?.GetProperty("Hook", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var asmProp = hookInfoType?.GetProperty("Assembly", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var delegateProp = hookInfoType?.GetProperty("Delegate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (hookProp == null || asmProp == null)
            {
                log.Warning("[HookSafety] Could not find Hook/Assembly properties on HookInfo.");
                return result;
            }

            // Iterate the ConcurrentDictionary values
            // trackedHooks is ConcurrentDictionary<Guid, HookInfo> — get the Values
            var valuesProperty = trackedHooks.GetType().GetProperty("Values");
            var values = valuesProperty?.GetValue(trackedHooks) as System.Collections.IEnumerable;
            if (values == null)
            {
                log.Warning("[HookSafety] Could not enumerate TrackedHooks values.");
                return result;
            }

            // Build a map of plugin assembly names for cross-referencing
            var assemblyToPlugin = BuildAssemblyToPluginMap();

            foreach (var hookInfo in values)
            {
                try
                {
                    var hook = hookProp.GetValue(hookInfo);
                    if (hook == null) continue;

                    // IDalamudHook has Address and IsDisposed
                    var isDisposedProp = hook.GetType().GetProperty("IsDisposed");
                    if (isDisposedProp != null && (bool)isDisposedProp.GetValue(hook)!)
                        continue;

                    var addressProp = hook.GetType().GetProperty("Address");
                    if (addressProp == null) continue;
                    var address = (nint)addressProp.GetValue(hook)!;

                    var assembly = asmProp.GetValue(hookInfo) as Assembly;
                    if (assembly == null) continue;

                    // HookInfo.Assembly is always Dalamud.dll (scoped provider quirk).
                    // The REAL plugin assembly is in Delegate.Method.DeclaringType.Assembly.
                    var hookDelegate = delegateProp?.GetValue(hookInfo) as Delegate;
                    var realAssembly = hookDelegate?.Method?.DeclaringType?.Assembly ?? assembly;
                    var asmName = realAssembly.GetName().Name ?? "";

                    // Skip our own plugin's hooks
                    if (asmName.Equals("CombatSimulator", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Try to resolve assembly name to a friendly plugin name
                    var pluginName = assemblyToPlugin.TryGetValue(asmName, out var pn) ? pn : asmName;

                    // Log for our checked addresses so the user can see the resolution
                    foreach (var func in functions)
                    {
                        if (func.Address == address)
                        {
                            var methodName = hookDelegate?.Method?.DeclaringType?.FullName + "::" + hookDelegate?.Method?.Name;
                            log.Info($"[HookSafety] {func.Name} → TrackedHook found: assembly='{asmName}', delegate='{methodName}', resolved='{pluginName}'");
                        }
                    }

                    if (!result.TryGetValue(address, out var list))
                    {
                        list = new List<string>();
                        result[address] = list;
                    }

                    if (!list.Contains(pluginName))
                        list.Add(pluginName);
                }
                catch { }
            }

            log.Info($"[HookSafety] Queried {result.Count} unique hooked addresses from Dalamud HookManager.");

            // Log any TrackedHooks entries near our checked addresses (within ±0x100)
            foreach (var func in functions)
            {
                foreach (var (addr, names) in result)
                {
                    var diff = Math.Abs(addr - func.Address);
                    if (diff > 0 && diff <= 0x100)
                        log.Info($"[HookSafety] NEAR MISS: {func.Name} at 0x{func.Address:X}, TrackedHook at 0x{addr:X} (diff={diff}) by {string.Join(", ", names)}");
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[HookSafety] Failed to query Dalamud HookManager via reflection.");
        }

        return result;
    }

    /// <summary>
    private HashSet<string> GetLoadedPluginNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in pluginInterface.InstalledPlugins)
                if (p.IsLoaded)
                    names.Add(p.InternalName);
        }
        catch { }
        return names;
    }

    /// Build a map of assembly name → plugin display name using InstalledPlugins.
    /// Plugin DLLs are typically named after the InternalName (e.g., "RotationSolver.Basic" → "RotationSolver").
    /// </summary>
    private Dictionary<string, string> BuildAssemblyToPluginMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var plugin in pluginInterface.InstalledPlugins)
            {
                if (!plugin.IsLoaded) continue;

                // Map both InternalName and Name
                map[plugin.InternalName] = plugin.Name;

                // Also map common assembly name patterns:
                // "RotationSolver.Basic" → "RotationSolver (Rotation Solver)"
                // "VFXEditor" → "VFXEditor"
            }
        }
        catch { }
        return map;
    }

}
