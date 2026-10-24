// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;

namespace CombatSimulator.ActionCombat;

/// <summary>
/// The game's own ground marker (Omen sheet, <c>vfx/omen/eff/*.avfx</c>) under each enemy area
/// marker, scaled and turned to the snapshotted shape and removed when the marker leaves.
/// <para>
/// Safety: a bad path handed to the loader can take down the game's file thread, so a path is
/// only ever used after <see cref="IDataManager.FileExists"/> confirmed it (here, at spawn time,
/// on top of the check the action data already did), and anything unexpected fails closed — no
/// omen, the ImGui overlay draws the area instead. The instance is bound to the LOCAL PLAYER, not
/// the caster: binding only gives the resource load a collection to resolve against (Penumbra),
/// the transform is written every frame anyway, and the player actor is never one of our
/// modified NPCs (other plugins hooking actor VFX creation have crashed on those).
/// </para>
/// </summary>
public sealed class EnemyOmenVfx : IDisposable
{
    // Generic markers for areas whose action has no omen of its own (player job AoEs). All three
    // are among the 57 omen files measured to exist; each is still FileExists-checked before use.
    private const string GenericCircle = "vfx/omen/eff/general_1bf.avfx";
    private const string GenericRect = "vfx/omen/eff/general02f.avfx";

    private readonly EnemyAreaMarkers markers;
    private readonly AnimationController animation;
    private readonly Func<uint, ActionData?> getActionData;
    private readonly IDataManager dataManager;
    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly Dictionary<int, nint> live = new();
    private readonly Dictionary<string, bool> existsCache = new(StringComparer.OrdinalIgnoreCase);

    public EnemyOmenVfx(
        EnemyAreaMarkers markers,
        AnimationController animation,
        Func<uint, ActionData?> getActionData,
        IDataManager dataManager,
        Configuration config,
        IPluginLog log)
    {
        this.markers = markers;
        this.animation = animation;
        this.getActionData = getActionData;
        this.dataManager = dataManager;
        this.config = config;
        this.log = log;
        markers.Added += OnAdded;
        markers.Removing += OnRemoving;
    }

    /// <summary>Whether a native omen is currently rendering for <paramref name="marker"/>.</summary>
    public bool HasOmen(EnemyAreaMarker marker)
        => live.TryGetValue(marker.Id, out var ptr) && animation.IsManualVfxAlive(ptr);

    /// <summary>Re-assert each omen's transform (actor-bound instances are re-posed by the game every frame).</summary>
    public void Tick()
    {
        if (live.Count == 0)
            return;

        foreach (var m in markers.Active)
        {
            if (!live.TryGetValue(m.Id, out var ptr))
                continue;
            if (!animation.IsManualVfxAlive(ptr))
            {
                live.Remove(m.Id); // destroyed underneath us (zone change); overlay takes over
                continue;
            }
            var (pos, rot, scale) = Transform(m.Shape);
            animation.UpdateManualVfx(ptr, pos, rot, scale);
        }
    }

    private void OnAdded(EnemyAreaMarker m)
    {
        if (!config.EnemyNativeOmenVfx || !m.Shape.IsArea || !animation.StaticVfxAvailable)
            return;

        try
        {
            var path = ResolvePath(m);
            if (path == null)
                return;

            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player == null || player.Address == nint.Zero)
                return;

            var ptr = animation.CreateManualVfx(path, player.Address);
            if (ptr == nint.Zero)
                return;

            live[m.Id] = ptr;
            var (pos, rot, scale) = Transform(m.Shape);
            animation.UpdateManualVfx(ptr, pos, rot, scale);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[Omen] spawn failed for action {m.ActionId}; overlay only.");
        }
    }

    private void OnRemoving(EnemyAreaMarker m)
    {
        if (!live.Remove(m.Id, out var ptr))
            return;
        try
        {
            animation.RemoveManualVfx(ptr);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Omen] remove failed.");
        }
    }

    private string? ResolvePath(EnemyAreaMarker m)
    {
        var data = getActionData(m.ActionId);
        var own = data?.OmenVfxPath;
        if (!string.IsNullOrEmpty(own) && Exists(own))
            return own;

        // No omen of its own: a generic marker of the same shape, or none (donut → overlay).
        string? generic = m.Shape.Kind switch
        {
            TelegraphKind.Circle => GenericCircle,
            TelegraphKind.Rect => GenericRect,
            TelegraphKind.Cone => $"vfx/omen/eff/gl_fan{(int)MathF.Round(float.RadiansToDegrees(m.Shape.HalfAngleRad * 2f)):000}_1bf.avfx",
            _ => null,
        };
        return generic != null && Exists(generic) ? generic : null;
    }

    private bool Exists(string path)
    {
        if (existsCache.TryGetValue(path, out var known))
            return known;
        bool exists;
        try { exists = dataManager.FileExists(path); }
        catch { exists = false; }
        existsCache[path] = exists;
        return exists;
    }

    // Omen files are authored at unit size facing +Z: circles/cones/donuts scale by the radius,
    // rectangles by half-width across and length forward (same convention the reference sim uses).
    private static (Vector3 Pos, Quaternion Rot, Vector3 Scale) Transform(in TelegraphShape s)
    {
        var rot = Quaternion.CreateFromYawPitchRoll(s.Rotation, 0f, 0f);
        var scale = s.Kind == TelegraphKind.Rect
            ? new Vector3(s.HalfWidth, 1f, MathF.Max(0.1f, s.Radius))
            : new Vector3(MathF.Max(0.1f, s.Radius), 1f, MathF.Max(0.1f, s.Radius));
        return (s.Origin, rot, scale);
    }

    public void Dispose()
    {
        markers.Added -= OnAdded;
        markers.Removing -= OnRemoving;
        foreach (var ptr in live.Values)
        {
            try { animation.RemoveManualVfx(ptr); }
            catch { /* teardown */ }
        }
        live.Clear();
    }
}
