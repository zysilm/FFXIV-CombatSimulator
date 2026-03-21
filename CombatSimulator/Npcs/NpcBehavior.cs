using System.Collections.Generic;

namespace CombatSimulator.Npcs;

public enum NpcBehaviorType
{
    TrainingDummy,
    BasicMelee,
    BasicRanged,
    Boss,
}

public class NpcBehavior
{
    public float AutoAttackDelay { get; set; } = 3.0f;
    public float AutoAttackRange { get; set; } = 3.0f;
    public int AutoAttackPotency { get; set; } = 110;
    public uint AutoAttackActionId { get; set; } = 7;
    public float AggroRange { get; set; } = 0f;
    public float LeashDistance { get; set; } = 40f;
    public float MoveSpeed { get; set; } = 6f;
    public List<NpcSkill> Skills { get; set; } = new();

    public static NpcBehavior Create(NpcBehaviorType type)
    {
        return type switch
        {
            NpcBehaviorType.TrainingDummy => new NpcBehavior
            {
                AutoAttackDelay = float.MaxValue,
                LeashDistance = float.MaxValue,
                Skills = new(),
            },
            NpcBehaviorType.BasicMelee => new NpcBehavior
            {
                AutoAttackDelay = 3.0f,
                AutoAttackRange = 3.0f,
                AutoAttackPotency = 110,
                MoveSpeed = 6.0f,
                LeashDistance = 40.0f,
                Skills = new()
                {
                    new NpcSkill
                    {
                        Name = "Heavy Swing",
                        ActionId = 31,
                        Potency = 200,
                        Cooldown = 15.0f,
                        CastTime = 2.0f,
                        Range = 3.0f,
                        Priority = 1,
                    },
                },
            },
            NpcBehaviorType.BasicRanged => new NpcBehavior
            {
                AutoAttackDelay = 3.0f,
                AutoAttackRange = 25.0f,
                AutoAttackPotency = 80,
                MoveSpeed = 5.0f,
                LeashDistance = 40.0f,
                Skills = new()
                {
                    new NpcSkill
                    {
                        Name = "Fire",
                        ActionId = 141,
                        Potency = 300,
                        Cooldown = 8.0f,
                        CastTime = 2.5f,
                        Range = 25.0f,
                        Priority = 2,
                    },
                    new NpcSkill
                    {
                        Name = "Thunder",
                        ActionId = 144,
                        Potency = 150,
                        Cooldown = 30.0f,
                        CastTime = 0f,
                        Range = 25.0f,
                        Priority = 1,
                    },
                },
            },
            NpcBehaviorType.Boss => new NpcBehavior
            {
                AutoAttackDelay = 2.5f,
                AutoAttackRange = 3.0f,
                AutoAttackPotency = 200,
                MoveSpeed = 7.0f,
                LeashDistance = 60.0f,
                Skills = new()
                {
                    new NpcSkill
                    {
                        Name = "Mighty Strike",
                        ActionId = 31,
                        Potency = 400,
                        Cooldown = 20.0f,
                        CastTime = 3.0f,
                        Range = 3.0f,
                        Priority = 3,
                    },
                    new NpcSkill
                    {
                        Name = "Whirlwind",
                        ActionId = 41,
                        Potency = 250,
                        Cooldown = 12.0f,
                        CastTime = 2.0f,
                        Range = 5.0f,
                        Radius = 8.0f,
                        Priority = 2,
                    },
                    new NpcSkill
                    {
                        Name = "Enrage",
                        ActionId = 42,
                        Potency = 500,
                        Cooldown = 45.0f,
                        CastTime = 4.0f,
                        Range = 3.0f,
                        Priority = 1,
                        HpThreshold = 0.3f,
                    },
                },
            },
            _ => new NpcBehavior(),
        };
    }
}

public class NpcSkill
{
    public uint ActionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Potency { get; set; }
    public float Cooldown { get; set; }
    public float CastTime { get; set; }
    public float Range { get; set; }
    public float Radius { get; set; }
    public bool Interruptible { get; set; } = true;
    public int Priority { get; set; }
    public float HpThreshold { get; set; } = 1.0f;

    // Runtime state
    public float CooldownRemaining { get; set; }
}
