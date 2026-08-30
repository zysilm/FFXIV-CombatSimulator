// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Npcs;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Effects;

/// <summary>
/// Applies a relative visual scale to active target NPCs while preserving each actor's authored
/// BNpc/model scale. Every captured transform is restored when the actor leaves the active set or
/// the effect is disabled/reset.
/// </summary>
public sealed unsafe class NpcScaleController : IDisposable
{
    private readonly Configuration config;
    private readonly NpcSelector npcSelector;
    private readonly IObjectTable objectTable;
    private readonly Dictionary<nint, OriginalScale> originalScales = new();

    public NpcScaleController(Configuration config, NpcSelector npcSelector, IObjectTable objectTable)
    {
        this.config = config;
        this.npcSelector = npcSelector;
        this.objectTable = objectTable;
    }

    public void Tick()
    {
        var factor = Math.Clamp(config.NpcScale, 0.01f, 3.0f);
        if (!config.EnableNpcScale || MathF.Abs(factor - 1.0f) < 0.0001f)
        {
            RestoreAll();
            return;
        }

        var activeAddresses = new HashSet<nint>();
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.BattleChara == null || npc.Address == nint.Zero)
                continue;

            var gameObject = (GameObject*)npc.BattleChara;
            if (gameObject->DrawObject == null)
                continue;

            activeAddresses.Add(npc.Address);
            if (!originalScales.TryGetValue(npc.Address, out var captured) ||
                captured.EntityId != npc.SimulatedEntityId)
            {
                captured = new OriginalScale(npc.SimulatedEntityId, gameObject->DrawObject->Scale);
                originalScales[npc.Address] = captured;
            }

            gameObject->DrawObject->Scale = captured.Scale * factor;
            gameObject->DrawObject->NotifyTransformChanged();
        }

        RestoreActorsOutside(activeAddresses);
    }

    public void Reset()
    {
        // During logout/game shutdown the native world may already be gone. There is nothing left
        // to restore, and walking stale object pointers would be unsafe.
        if (objectTable.LocalPlayer is null)
        {
            originalScales.Clear();
            return;
        }

        RestoreAll();
    }

    private void RestoreActorsOutside(HashSet<nint> activeAddresses)
    {
        if (originalScales.Count == 0)
            return;

        var restore = new HashSet<nint>();
        foreach (var address in originalScales.Keys)
            if (!activeAddresses.Contains(address))
                restore.Add(address);

        Restore(restore);
    }

    private void RestoreAll()
    {
        if (originalScales.Count == 0)
            return;

        Restore(new HashSet<nint>(originalScales.Keys));
    }

    private void Restore(HashSet<nint> addresses)
    {
        if (addresses.Count == 0)
            return;

        foreach (var actor in objectTable)
        {
            if (actor is null || actor.Address == nint.Zero || !addresses.Contains(actor.Address) ||
                !originalScales.TryGetValue(actor.Address, out var captured) ||
                actor.EntityId != captured.EntityId)
                continue;

            var gameObject = (GameObject*)actor.Address;
            if (gameObject->DrawObject == null)
                continue;

            gameObject->DrawObject->Scale = captured.Scale;
            gameObject->DrawObject->NotifyTransformChanged();
        }

        foreach (var address in addresses)
            originalScales.Remove(address);
    }

    public void Dispose() => Reset();

    private readonly record struct OriginalScale(uint EntityId, Vector3 Scale);
}
