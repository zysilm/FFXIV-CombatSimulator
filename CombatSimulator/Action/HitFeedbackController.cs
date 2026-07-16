using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Npcs;
using Dalamud.Plugin.Services;

namespace CombatSimulator.ActionCombat;

/// <summary>
/// Centralizes the impact feedback of a confirmed player hit: a brief target hitstop, a
/// decaying camera punch, and an optional hit spark. Pure feedback — it never changes combat
/// outcomes. The camera offset is exposed for the camera hook to layer on top of the final view.
/// </summary>
public sealed class HitFeedbackController
{
    private readonly Configuration config;
    private readonly Func<uint, SimulatedNpc?> resolveNpc;
    private readonly AnimationController anim;
    private readonly IPluginLog log;

    // Camera shake state.
    private float shakeTime;
    private float shakeDuration;
    private float shakeMagnitude;
    private uint rng = 0x9E3779B9;

    // Hitstop bookkeeping: target entity id -> remaining freeze time (s). Re-resolved on restore so
    // a target that died (and is now ragdolled) is left alone instead of being un-frozen.
    private readonly Dictionary<uint, float> hitstop = new();

    // Scheduled hits: feedback is delayed from input until the weapon visually connects. Stored by
    // entity id so the target is re-resolved at fire time (skipped if it died/despawned meanwhile).
    private struct PendingHit { public uint EntityId; public float Timer; }
    private readonly List<PendingHit> pending = new();

    /// <summary>Current camera-shake world offset; read by the camera hook each frame.</summary>
    public Vector3 CurrentShakeOffset { get; private set; }

    public HitFeedbackController(
        Configuration config,
        Func<uint, SimulatedNpc?> resolveNpc,
        AnimationController anim,
        IPluginLog log)
    {
        this.config = config;
        this.resolveNpc = resolveNpc;
        this.anim = anim;
        this.log = log;
    }

    /// <summary>Schedule feedback for a confirmed hit on <paramref name="target"/>. Fired after
    /// <see cref="Configuration.HitFeedbackDelay"/> so it lands when the weapon visually connects.</summary>
    public void TriggerHit(SimulatedNpc? target)
    {
        if (target == null || !config.EnableHitFeedback || target.Address == 0)
            return;

        var delay = MathF.Max(0f, config.HitFeedbackDelay);
        if (delay <= 0.001f)
        {
            FireHit(target);
            return;
        }
        pending.Add(new PendingHit { EntityId = target.State.EntityId, Timer = delay });
    }

    /// <summary>Actually apply the feedback (camera + spark + hitstop) on the target now.</summary>
    private void FireHit(SimulatedNpc target)
    {
        var addr = target.Address;
        if (addr == 0)
            return;

        if (config.EnableHitSparkVfx)
            anim.SpawnHitSpark(addr);

        if (config.HitCameraShake > 0f)
        {
            shakeMagnitude = config.HitCameraShake;
            shakeDuration = MathF.Max(0.02f, config.HitCameraShakeDuration);
            shakeTime = shakeDuration;
        }

        if (config.HitstopMs > 0f && target.IsAlive)
        {
            anim.SetActorAnimationSpeed(addr, 0f);          // freeze the struck target
            hitstop[target.State.EntityId] = config.HitstopMs / 1000f;
        }
    }

    // A corpse hitting the ground shakes the view like anything else that hits hard. Half of what makes
    // an impact read heavy happens outside the body — take the camera away and the very same landing
    // goes back to looking weightless.
    private const float ImpactShakePerSpeed = 0.055f; // shake units per m/s of descent
    private const float ImpactShakeMax = 0.55f;       // a long fall must not throw the view off the body
    private const float ImpactShakeDuration = 0.22f;

    /// <summary>Shake for a corpse landing, scaled by how hard it came down. Rides the same decay as a
    /// landed blow, so it belongs to the same game.</summary>
    public void ShakeForHardLanding(float descentSpeed)
    {
        var magnitude = MathF.Min(descentSpeed * ImpactShakePerSpeed, ImpactShakeMax);
        if (magnitude <= 0.001f) return;

        shakeMagnitude = magnitude;
        shakeDuration = ImpactShakeDuration;
        shakeTime = shakeDuration;
    }

    public void Tick(float dt)
    {
        // Scheduled hits: count down and fire when the weapon connects, re-resolving so a target
        // that died/despawned during the windup is simply skipped.
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            var p = pending[i];
            p.Timer -= dt;
            if (p.Timer > 0f)
            {
                pending[i] = p;
                continue;
            }
            pending.RemoveAt(i);
            var npc = resolveNpc(p.EntityId);
            if (npc != null && npc.IsAlive)
                FireHit(npc);
        }

        // Camera shake: high-frequency jitter that decays to zero.
        if (shakeTime > 0f)
        {
            shakeTime -= dt;
            var t = Math.Clamp(shakeTime / shakeDuration, 0f, 1f);
            var amp = shakeMagnitude * t * t;               // quadratic falloff
            CurrentShakeOffset = new Vector3(NextSigned() * amp, NextSigned() * amp * 0.6f, NextSigned() * amp);
        }
        else if (CurrentShakeOffset != Vector3.Zero)
        {
            CurrentShakeOffset = Vector3.Zero;
        }

        // Hitstop: count down and restore, re-resolving so a dead/despawned target is left to the
        // ragdoll/game instead of being force-unfrozen.
        if (hitstop.Count > 0)
        {
            List<uint>? expired = null;
            foreach (var id in hitstop.Keys)
            {
                var rem = hitstop[id] - dt;
                if (rem > 0f)
                    continue;
                (expired ??= new List<uint>()).Add(id);
            }
            // Decrement survivors (separate pass to avoid mutating during enumeration).
            if (expired == null)
            {
                foreach (var id in new List<uint>(hitstop.Keys))
                    hitstop[id] -= dt;
            }
            else
            {
                foreach (var id in new List<uint>(hitstop.Keys))
                    if (!expired.Contains(id))
                        hitstop[id] -= dt;

                foreach (var id in expired)
                {
                    hitstop.Remove(id);
                    var npc = resolveNpc(id);
                    if (npc != null && npc.IsAlive && npc.Address != 0)
                        anim.SetActorAnimationSpeed(npc.Address, 1f);
                }
            }
        }
    }

    /// <summary>Release every outstanding hitstop (e.g. on combat reset / dispose) so no enemy is
    /// left frozen.</summary>
    public void Clear()
    {
        foreach (var id in new List<uint>(hitstop.Keys))
        {
            var npc = resolveNpc(id);
            if (npc != null && npc.IsAlive && npc.Address != 0)
                anim.SetActorAnimationSpeed(npc.Address, 1f);
        }
        hitstop.Clear();
        pending.Clear();
        shakeTime = 0f;
        CurrentShakeOffset = Vector3.Zero;
    }

    // Cheap xorshift in [-1, 1].
    private float NextSigned()
    {
        rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
        return (rng / (float)uint.MaxValue) * 2f - 1f;
    }
}
