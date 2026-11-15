// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Safety;

/// <summary>
/// Two views of other plugins' hooks, both read from Dalamud's internal HookManager.TrackedHooks
/// (which maps every hook to the delegate, and so the plugin, that created it):
/// <list type="bullet">
/// <item>Functions we CALL (registered here): another plugin's detour runs inside our call, with our
/// simulated actors. The first bytes are also checked for a JMP detour left by a non-Dalamud patcher.</item>
/// <item>Functions we HOOK (discovered from TrackedHooks, no registration needed): which other plugins
/// hook the same address. Dalamud chains such hooks, so this is informational.</item>
/// </list>
/// </summary>
public class HookSafetyChecker
{
    private const string OwnAssemblyName = "CombatSimulator";

    private readonly IPluginLog log;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly List<CheckedFunction> functions = new();
    private readonly List<OwnHook> ownHooks = new();

    public IReadOnlyList<CheckedFunction> Functions => functions;
    public IReadOnlyList<OwnHook> OwnHooks => ownHooks;

    /// <summary>
    /// True when a function whose hooking we treat as a risk (<see cref="CheckedFunction.RaisesConflict"/>)
    /// is hooked by another plugin. Drives the status line and the Start confirmation.
    /// </summary>
    public bool HasConflicts { get; private set; }

    public class CheckedFunction
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        /// <summary>Re-run at each scan when set (virtual functions read from a live object's vtable).</summary>
        public Func<nint>? Resolve { get; init; }
        public nint Address { get; set; }
        public byte ExpectedFirstByte { get; init; }
        /// <summary>
        /// Whether another plugin hooking this function raises the conflict warning. False for functions
        /// the game itself calls for every actor it spawns, dresses, draws or poses: plugins such as
        /// Penumbra and Glamourer always hook several of those, so a warning would be permanent and mean
        /// nothing. Those rows still show who hooks them, for crash diagnosis.
        /// </summary>
        public bool RaisesConflict { get; init; } = true;
        public bool IsHooked { get; set; }
        public string DetourInfo { get; set; } = "";
        public string HookedBy { get; set; } = "";
    }

    /// <summary>One of our own hooks, and the other plugins that hook the same address.</summary>
    public class OwnHook
    {
        public string Name { get; init; } = "";
        public nint Address { get; init; }
        public string AlsoHookedBy { get; set; } = "";
    }

    public HookSafetyChecker(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public void Register(string name, string description, nint address, byte expectedFirstByte = 0, bool raisesConflict = true)
    {
        if (address == 0) return;
        functions.Add(new CheckedFunction
        {
            Name = name,
            Description = description,
            Address = address,
            ExpectedFirstByte = expectedFirstByte,
            RaisesConflict = raisesConflict,
        });
    }

    /// <summary>
    /// Register a function whose address is only known from a live object (a virtual function).
    /// <paramref name="resolve"/> runs at each scan; while it returns 0 the row is skipped.
    /// </summary>
    public void Register(string name, string description, Func<nint> resolve, bool raisesConflict = true)
    {
        functions.Add(new CheckedFunction
        {
            Name = name,
            Description = description,
            Resolve = resolve,
            RaisesConflict = raisesConflict,
        });
    }

    public void Scan()
    {
        foreach (var func in functions)
        {
            if (func.Resolve == null) continue;
            try { func.Address = func.Resolve(); }
            catch { func.Address = 0; }
        }

        // Dalamud's hook registry: address → other plugins hooking it. Also refreshes ownHooks.
        var hookOwners = QueryDalamudTrackedHooks();
        var ownAddresses = new HashSet<nint>(ownHooks.Select(h => h.Address));

        HasConflicts = false;
        foreach (var func in functions)
        {
            func.IsHooked = false;
            func.DetourInfo = "";
            func.HookedBy = "";
            if (func.Address == 0) continue;

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

                // Our own hook writes a JMP there too; that is not another plugin's residue.
                if (ownAddresses.Contains(func.Address))
                {
                    jmpDetected = false;
                    func.DetourInfo = "";
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
                    if (func.RaisesConflict)
                        HasConflicts = true;
                    var by = string.IsNullOrEmpty(func.HookedBy) ? "" : $" by {func.HookedBy}";
                    var kind = func.RaisesConflict ? "HOOKED" : "hooked (informational)";
                    log.Warning($"[HookSafety] {func.Name} {kind}{by} ({func.DetourInfo})");
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

        foreach (var own in ownHooks)
        {
            own.AlsoHookedBy = hookOwners.TryGetValue(own.Address, out var others) ? string.Join(", ", others) : "";
            if (own.AlsoHookedBy.Length > 0)
                log.Info($"[HookSafety] Our hook {own.Name} at 0x{own.Address:X} is also hooked by {own.AlsoHookedBy}");
        }

        log.Info($"[HookSafety] Scan complete: {(HasConflicts ? "CONFLICTS FOUND" : "CLEAN")}; " +
                 $"{functions.Count(f => f.IsHooked)}/{functions.Count(f => f.Address != 0)} called functions hooked by others, " +
                 $"{ownHooks.Count(h => h.AlsoHookedBy.Length > 0)}/{ownHooks.Count} of our hooks co-hooked.");
    }

    /// <summary>
    /// Access Dalamud's internal HookManager.TrackedHooks via reflection.
    /// Returns a map of hooked address → names of the OTHER plugins/assemblies that hook it, and
    /// rebuilds <see cref="ownHooks"/> from the entries whose detour lives in this plugin.
    /// </summary>
    private Dictionary<nint, List<string>> QueryDalamudTrackedHooks()
    {
        var result = new Dictionary<nint, List<string>>();
        ownHooks.Clear();

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

                    if (asmName.Equals(OwnAssemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        ownHooks.Add(new OwnHook { Name = DescribeOwnHook(hookDelegate), Address = address });
                        continue;
                    }

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

            ownHooks.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            log.Info($"[HookSafety] Queried {result.Count} unique addresses hooked by others, {ownHooks.Count} own hooks, from Dalamud HookManager.");

            // Log any TrackedHooks entries near our checked addresses (within ±0x100)
            foreach (var func in functions)
            {
                if (func.Address == 0) continue;
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

    /// <summary>"Class.Function" from our detour, e.g. UseActionHook.UseActionDetour → "UseActionHook.UseAction".</summary>
    private static string DescribeOwnHook(Delegate? detour)
    {
        var method = detour?.Method;
        if (method == null) return "(unknown)";
        var type = method.DeclaringType;
        while (type?.DeclaringType != null) // compiler-generated closure classes are nested
            type = type.DeclaringType;
        var name = method.Name;
        if (name.EndsWith("Detour", StringComparison.Ordinal) && name.Length > "Detour".Length)
            name = name[..^"Detour".Length];
        if (name.Contains('<')) name = "(lambda)";
        return $"{type?.Name ?? "?"}.{name}";
    }

    /// <summary>
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
                map[plugin.InternalName] = plugin.Name;
            }
        }
        catch { }
        return map;
    }
}
