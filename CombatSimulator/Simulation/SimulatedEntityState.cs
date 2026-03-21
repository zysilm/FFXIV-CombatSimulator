using System.Collections.Generic;

namespace CombatSimulator.Simulation;

public class SimulatedEntityState
{
    // Identity
    public uint EntityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsPlayer { get; set; }

    // Vitals
    public int CurrentHp { get; set; }
    public int MaxHp { get; set; }
    public int CurrentMp { get; set; }
    public int MaxMp { get; set; } = 10000;
    public int Level { get; set; }
    public bool IsAlive => CurrentHp > 0;

    // Stats
    public int MainStat { get; set; }
    public int Determination { get; set; }
    public int CriticalHit { get; set; }
    public int DirectHit { get; set; }
    public int Defense { get; set; }
    public int MagicDefense { get; set; }
    public int AttackPower { get; set; }
    public int AttackMagicPotency { get; set; }

    // Combat state
    public float AnimationLock { get; set; }
    public float GcdRemaining { get; set; }
    public float AutoAttackTimer { get; set; }
    public bool IsCasting { get; set; }
    public float CastTimeElapsed { get; set; }
    public float CastTimeTotal { get; set; }
    public uint CastActionId { get; set; }
    public ulong CastTargetId { get; set; }

    // Combo
    public uint LastComboAction { get; set; }
    public float ComboTimer { get; set; }

    // Cooldowns (keyed by recast group)
    public Dictionary<int, RecastState> Cooldowns { get; } = new();

    // Status effects
    public List<SimulatedStatus> StatusEffects { get; } = new();

    public void Reset()
    {
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
        LastComboAction = 0;
        ComboTimer = 0;
        Cooldowns.Clear();
        StatusEffects.Clear();
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
