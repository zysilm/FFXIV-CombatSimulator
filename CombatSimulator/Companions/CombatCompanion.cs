using System.Numerics;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace CombatSimulator.Companions;

public unsafe class CombatCompanion
{
    public uint SimulatedEntityId { get; set; }
    public int ObjectIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint SourceEntityId { get; set; }
    public BattleChara* BattleChara { get; set; }
    public nint Address => (nint)BattleChara;
    public IGameObject? GameObjectRef { get; set; }
    public SimulatedEntityState State { get; set; } = new();
    public NpcBehavior Behavior { get; set; } = new();
    public Vector3 SpawnPosition { get; set; }
    public bool IsSpawned { get; set; }
    public bool IsRanged { get; set; }
    public float AutoAttackTimer { get; set; }
    public float EngageDelayTimer { get; set; }
    public NpcSkill? CurrentCastSkill { get; set; }
    public uint CurrentTargetId { get; set; }
    public float RecentDamage { get; set; }
    public float RecentDps { get; set; }
    public bool MoveAnimActive { get; set; }
    public bool DeathAnimationPlayed { get; set; }
    public bool IsAlive => State.IsAlive;
}
