// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;

namespace CombatSimulator.Simulation;

public class SimulatedEntityState
{
    // Identity
    public uint EntityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsPlayer { get; set; }
    public bool IsCompanion { get; set; }

    /// <summary>
    /// Of the player's side: held at 1 HP, kneeling in the chosen emote, unable to act, until an
    /// enemy executes them or a friend frees them (War Generator hostages).
    /// </summary>
    public bool IsCaptive { get; set; }
    /// <summary>Died by execution while captive: the body drops at once, from the kneel.</summary>
    public bool Executed { get; set; }
    /// <summary>Times the damage enemies do to this one (one raising a friend takes more).</summary>
    public float IncomingDamageScale { get; set; } = 1f;

    // Vitals
    public int CurrentHp { get; set; }
    public int MaxHp { get; set; }
    public int CurrentMp { get; set; }
    public int MaxMp { get; set; } = 10000;
    public int Level { get; set; }
    /// <summary>For an enemy: CombatBudgetModel.LevelFactor of its level against the player's.</summary>
    public float LevelFactor { get; set; } = 1f;
    public bool IsAlive => CurrentHp > 0;

    // Combat numbers (see CombatBudgetModel). No character stats: gear, level and job do not enter a fight.
    public uint ClassJobId { get; set; }
    public bool IsTank { get; set; }
    /// <summary>How strong an enemy is. Unused for the player and companions.</summary>
    public EnemyTier Tier { get; set; } = EnemyTier.Normal;
    // Combat state
    public float AnimationLock { get; set; }
    public float GcdRemaining { get; set; }
    public float AutoAttackTimer { get; set; }
    public bool IsCasting { get; set; }
    public float CastTimeElapsed { get; set; }
    public float CastTimeTotal { get; set; }
    public uint CastActionId { get; set; }
    public ulong CastTargetId { get; set; }
    /// <summary>The current cast can be stopped by an interrupt (stuns stop any cast).</summary>
    public bool CastInterruptible { get; set; }

    // Combo
    public uint LastComboAction { get; set; }
    public float ComboTimer { get; set; }

    // Cooldowns (keyed by recast group)
    public Dictionary<int, RecastState> Cooldowns { get; } = new();

    // Status effects
    public List<SimulatedStatus> StatusEffects { get; } = new();

    // Sub-point MP carried between frames. Per-frame regen at high framerates is well under one
    // point, so truncating each frame would round it away entirely.
    private float mpRegenRemainder;

    /// <summary>Regenerate MP at <paramref name="perSecond"/>, independent of framerate.</summary>
    public void RegenerateMp(float perSecond, float deltaTime)
    {
        if (CurrentMp >= MaxMp)
        {
            mpRegenRemainder = 0f;
            return;
        }

        mpRegenRemainder += perSecond * deltaTime;
        var whole = (int)mpRegenRemainder;
        if (whole <= 0)
            return;

        mpRegenRemainder -= whole;
        CurrentMp = System.Math.Min(MaxMp, CurrentMp + whole);
    }

    public void Reset()
    {
        IsCaptive = false;
        Executed = false;
        IncomingDamageScale = 1f;
        CurrentHp = MaxHp;
        CurrentMp = MaxMp;
        AnimationLock = 0;
        GcdRemaining = 0;
        AutoAttackTimer = 0;
        IsCasting = false;
        CastTimeElapsed = 0;
        CastTimeTotal = 0;
        CastActionId = 0;
        CastTargetId = 0;
        CastInterruptible = false;
        LastComboAction = 0;
        ComboTimer = 0;
        Cooldowns.Clear();
        StatusEffects.Clear();
        mpRegenRemainder = 0f;
    }
}

public class RecastState
{
    public uint ActionId { get; set; }
    public float Elapsed { get; set; }
    public float Total { get; set; }
    public bool IsActive => Elapsed < Total;
    public float Remaining => Total - Elapsed;
}

public class SimulatedStatus
{
    public uint StatusId { get; set; }
    public string Name { get; set; } = string.Empty;
    public float Duration { get; set; }
    public float Remaining { get; set; }
    public int Stacks { get; set; }
    public uint SourceId { get; set; }
    public float Potency { get; set; }
    public bool IsDot { get; set; }
    public bool IsHot { get; set; }
}
