using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Safety;

/// <summary>
/// Detects whether native game functions are hooked by other plugins
/// by inspecting the first bytes at each function address for JMP detours.
/// </summary>
public class HookSafetyChecker
{
    private readonly IPluginLog log;
    private readonly List<CheckedFunction> functions = new();

    public IReadOnlyList<CheckedFunction> Functions => functions;
    public bool IsUnsafe { get; private set; }

    public class CheckedFunction
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public nint Address { get; init; }
        public bool IsHooked { get; set; }
        public string DetourInfo { get; set; } = "";
    }

    public HookSafetyChecker(IPluginLog log)
    {
        this.log = log;
    }

    /// <summary>
    /// Register a native function address to be checked for third-party hooks.
    /// </summary>
    public void Register(string name, string description, nint address)
    {
        if (address == 0) return;
        functions.Add(new CheckedFunction
        {
            Name = name,
            Description = description,
            Address = address,
        });
    }

    /// <summary>
    /// Scan all registered function addresses for JMP detours.
    /// Call this after all plugins have loaded (e.g., on first framework tick).
    /// </summary>
    public void Scan()
    {
        IsUnsafe = false;
        foreach (var func in functions)
        {
            func.IsHooked = false;
            func.DetourInfo = "";

            try
            {
                var b0 = Marshal.ReadByte(func.Address);
                var b1 = Marshal.ReadByte(func.Address + 1);

                if (b0 == 0xE9)
                {
                    // JMP rel32 — standard MinHook/Reloaded detour
                    var rel = Marshal.ReadInt32(func.Address + 1);
                    var target = func.Address + 5 + rel;
                    func.IsHooked = true;
                    func.DetourInfo = $"JMP rel32 -> 0x{target:X}";
                }
                else if (b0 == 0xFF && b1 == 0x25)
                {
                    // JMP [rip+disp32] — indirect jump
                    var disp = Marshal.ReadInt32(func.Address + 2);
                    var ptrAddr = func.Address + 6 + disp;
                    var target = Marshal.ReadIntPtr(ptrAddr);
                    func.IsHooked = true;
                    func.DetourInfo = $"JMP [rip+0x{disp:X}] -> 0x{target:X}";
                }

                if (func.IsHooked)
                {
                    IsUnsafe = true;
                    log.Warning($"[HookSafety] {func.Name} at 0x{func.Address:X} is HOOKED by another plugin ({func.DetourInfo})");
                }
                else
                {
                    log.Info($"[HookSafety] {func.Name} at 0x{func.Address:X} — clean (first bytes: {b0:X2} {b1:X2})");
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"[HookSafety] Failed to read bytes at {func.Name} (0x{func.Address:X})");
            }
        }

        log.Info($"[HookSafety] Scan complete: {(IsUnsafe ? "UNSAFE" : "SAFE")}");
    }
}
