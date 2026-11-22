// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace CombatSimulator.Effects;

/// <summary>
/// Stops the cold-zone breath on bodies that are dead in the simulation.
/// <para>
/// In cold zones the game spawns a breath VFX (VFX row 301 <c>wh_breath00f</c> or 478 <c>wh_breath01f</c>) on
/// characters from a per-frame VfxContainer helper (this = the container, <c>*(this + 8)</c> = its owner): after
/// its checks (cold level, render flags, model type, distance &lt; 20) it returns early when the owner
/// <c>IsDead()</c> (<c>Mode == Dead</c>), otherwise it accumulates a timer at <c>this + 0xE8</c> and spawns the
/// VFX every 2.5 s, fire-and-forget. Our deaths are simulated (Mode stays Normal), so the game keeps breathing for
/// our bodies. This hook returns before the original for an owner that is dead in the simulation, exactly like the
/// game's own early return (the timer does not advance). It never writes game state.
/// </para>
/// Offline disassembly of the 2026-11-10 game build: the function returns nothing (every path ends in the same
/// epilogue with whatever is in rax), so it is hooked as void.
/// </summary>
public sealed unsafe class ColdBreathSuppressor : IDisposable
{
    private const string Signature =
        "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 30 48 8B 05 ?? ?? ?? ?? 33 F6 48 8B D9 8B FE 48 85 C0 74 ?? 0F B6 40 33";

    private delegate void ColdBreathDelegate(nint vfxContainer);

    private readonly CombatEngine engine;
    private readonly NpcSelector npcSelector;
    private readonly CombatCompanionManager companions;
    private readonly IPluginLog log;
    private readonly Hook<ColdBreathDelegate>? hook;

    /// <summary>The game's static pointer whose byte at +0x33 is the zone's cold level (0 = not cold); 0 when unknown.</summary>
    private readonly nint coldLevelPointer;

    // Bodies dead in the simulation, with their names, rebuilt at most once per game frame.
    private readonly Dictionary<nint, string> deadBodies = new();
    private uint deadBodiesFrame;
    private bool deadBodiesBuilt;

    // Bodies already logged in this death; an owner leaves the set the first time it breathes again.
    private readonly HashSet<nint> logged = new();
    private bool detourFailureLogged;

    // Diagnostics: while the player is dead, the first owner of each kind (clone skipped, dead body skipped,
    // client actor let through) is logged once per session, so an in-game test shows who still breathes.
    private readonly HashSet<string> diagnosedKinds = new();

    /// <summary>First object-table index of client-created actors (ClientObjectManager slot 0).</summary>
    private const int ClientObjectIndexStart = 200;

    // Whether an actor address is one of our hidden helper clones (DismembermentController).
    private readonly Func<nint, bool> isHelperClone;

    public ColdBreathSuppressor(IGameInteropProvider gameInterop, ISigScanner sigScanner, CombatEngine engine,
        NpcSelector npcSelector, CombatCompanionManager companions, Func<nint, bool> isHelperClone, IPluginLog log)
    {
        this.isHelperClone = isHelperClone;
        this.engine = engine;
        this.npcSelector = npcSelector;
        this.companions = companions;
        this.log = log;

        nint address;
        try
        {
            address = sigScanner.ScanText(Signature);
        }
        catch (Exception)
        {
            log.Warning("ColdBreath: the cold-breath function was not found in this game version; simulated bodies keep breathing (feature off).");
            return;
        }

        // "mov rax, [rip + disp32]" at +0xF: the static pointer whose byte +0x33 the game reads as the cold level.
        try
        {
            var mov = (byte*)(address + 0xF);
            if (mov[0] == 0x48 && mov[1] == 0x8B && mov[2] == 0x05)
                coldLevelPointer = address + 0x16 + *(int*)(address + 0x12);
        }
        catch (Exception)
        {
            coldLevelPointer = 0;
        }

        try
        {
            hook = gameInterop.HookFromAddress<ColdBreathDelegate>(address, Detour);
            hook.Enable();
            var offset = address - sigScanner.Module.BaseAddress;
            log.Info($"ColdBreath: hook installed at 0x{address:X} (module offset 0x{offset:X}); simulated bodies stop breathing in cold zones.");
        }
        catch (Exception ex)
        {
            hook = null;
            log.Warning(ex, "ColdBreath: could not hook the cold-breath function; simulated bodies keep breathing (feature off).");
        }
    }

    private void Detour(nint vfxContainer)
    {
        try
        {
            var owner = vfxContainer != 0 ? *(nint*)(vfxContainer + 8) : 0;

            // Our hidden helper clones (separated parts, armour and garment pieces) never breathe, in or out of a
            // battle. Matched by address against the live clone list, which is filled before a clone is drawn.
            if (owner != 0 && isHelperClone(owner))
            {
                Diagnose("clone skipped", owner);
                return;
            }

            if (engine.IsActive && owner != 0)
            {
                RefreshDeadBodies();
                if (deadBodies.TryGetValue(owner, out var name))
                {
                    if (IsColdZone() && logged.Add(owner))
                        log.Info($"ColdBreath: suppressed on {name} (simulated death)");
                    Diagnose("dead body skipped", owner);
                    return; // the game's own "owner is dead" early return
                }

                logged.Remove(owner);
                Diagnose("client actor breathes", owner);
            }
            else if (!engine.IsActive && logged.Count > 0)
            {
                logged.Clear(); // the battle is over: every body is back to normal
            }
        }
        catch (Exception ex)
        {
            if (!detourFailureLogged)
            {
                detourFailureLogged = true;
                log.Warning(ex, "ColdBreath: the check failed; the game's breath runs unchanged.");
            }
        }

        hook!.Original(vfxContainer);
    }

    /// <summary>
    /// Logs the first owner of each kind seen while the player is dead in a cold zone: the kinds that are
    /// skipped, and client-created actors (spawned enemies, allies, spectators) that are let through.
    /// </summary>
    private void Diagnose(string kind, nint owner)
    {
        if (diagnosedKinds.Contains(kind) || !engine.IsActive || engine.State.PlayerState.IsAlive || !IsColdZone())
            return;
        var obj = (GameObject*)owner;
        int index = obj->ObjectIndex;
        if (kind == "client actor breathes" && index < ClientObjectIndexStart)
            return; // only client-created actors are of interest
        diagnosedKinds.Add(kind);
        log.Info($"ColdBreath: while the player is dead, {kind}: object idx={index} '{obj->NameString}' kind={obj->ObjectKind}");
    }

    private bool IsColdZone()
    {
        if (coldLevelPointer == 0) return true; // unknown: log the first skip
        var p = *(nint*)coldLevelPointer;
        return p != 0 && *(byte*)(p + 0x33) != 0;
    }

    /// <summary>Who is dead in the simulation: the player, enemies (spawned, selected or map) and allies.</summary>
    private void RefreshDeadBodies()
    {
        var framework = Framework.Instance();
        var frame = framework != null ? framework->FrameCounter : 0u;
        if (deadBodiesBuilt && frame == deadBodiesFrame && framework != null) return;
        deadBodiesFrame = frame;
        deadBodiesBuilt = true;
        deadBodies.Clear();

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player != null && player.Address != 0 && !engine.State.PlayerState.IsAlive)
            deadBodies[player.Address] = player.Name.TextValue;

        foreach (var npc in npcSelector.SelectedNpcs)
            if (npc.Address != 0 && !npc.State.IsAlive)
                deadBodies[npc.Address] = npc.Name;

        foreach (var ally in companions.Companions)
            if (ally.Address != 0 && !ally.State.IsAlive)
                deadBodies[ally.Address] = ally.Name;
    }

    public void Dispose()
    {
        hook?.Dispose();
        deadBodies.Clear();
        logged.Clear();
        diagnosedKinds.Clear();
    }
}
