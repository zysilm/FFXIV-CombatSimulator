// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Npcs;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace CombatSimulator.Simulation;

/// <summary>
/// Bow and gun auto-attacks connect late: the shot motion draws and releases first, then the arrow
/// has to fly. A melee swing hides that gap behind the hit-feedback delay; a shot cannot, so its
/// damage, flinch and death are held until the arrow would arrive while the motion plays at once.
/// </summary>
public partial class CombatEngine
{
    /// <summary>Seconds from the shot motion starting to the arrow or bullet leaving (frame 18 of
    /// the shared shot timeline).</summary>
    private const float ShotReleaseSeconds = 0.6f;
    private const float ArrowSpeed = 45f;    // m/s
    private const float BulletSpeed = 150f;  // m/s

    private readonly List<(float Timer, Action Resolve)> deferredHits = new();

    /// <summary>
    /// How long after the attack starts a ranged auto-attack by <paramref name="source"/> actually
    /// connects with <paramref name="target"/>; negative when the source does not wield a bow or gun.
    /// </summary>
    private unsafe float RangedContactDelay(SimulatedEntityState source, SimulatedEntityState target)
    {
        var address = ResolveTargetAddress(source);
        if (address == nint.Zero)
            return -1f;
        var character = (Character*)address;
        if (NpcWeaponClassifier.DetectFromCharacter(character) != NpcAttackStyle.Ranged)
            return -1f;

        var distance = Vector3.Distance(GetEntityPosition(source), GetEntityPosition(target));
        if (!float.IsFinite(distance))
            distance = 0f;
        var speed = NpcWeaponClassifier.IsBowWielder(character) ? ArrowSpeed : BulletSpeed;
        return ShotReleaseSeconds + distance / speed;
    }

    private void DeferHit(float delay, Action resolve)
        => deferredHits.Add((MathF.Max(0f, delay), resolve));

    private void TickDeferredHits(float deltaTime)
    {
        for (var i = deferredHits.Count - 1; i >= 0; i--)
        {
            var (timer, resolve) = deferredHits[i];
            timer -= deltaTime;
            if (timer > 0f)
            {
                deferredHits[i] = (timer, resolve);
                continue;
            }
            deferredHits.RemoveAt(i);
            try { resolve(); }
            catch (Exception ex) { log.Error(ex, "Deferred ranged hit failed to resolve."); }
        }
    }

    private void ClearDeferredHits() => deferredHits.Clear();
}
