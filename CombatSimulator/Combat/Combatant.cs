// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace CombatSimulator.Combat;

/// <summary>Which side of the fight a combatant is on.</summary>
public enum CombatSide
{
    Player,
    Ally,
    Enemy,
}

/// <summary>
/// What an enemy (<see cref="SimulatedNpc"/>) and an ally (<see cref="Companions.CombatCompanion"/>)
/// have in common: every member both declared with the same name, type and meaning
/// (docs/PLUGIN_ARCHITECTURE.md §2.1). Side-specific members stay on the two classes.
/// </summary>
public abstract unsafe class Combatant
{
    // Identity
    public uint SimulatedEntityId { get; set; }
    public int ObjectIndex { get; set; }
    public string Name { get; set; } = string.Empty;

    // Game object reference
    public BattleChara* BattleChara { get; set; }
    public nint Address => (nint)BattleChara;
    public IGameObject? GameObjectRef { get; set; }

    /// <summary>Collision/hitbox radius (yalms) of the underlying game object. Used for edge-to-edge
    /// melee range so large enemies are reachable from their surface, not their centre. 0.5 fallback.</summary>
    public float HitboxRadius
    {
        get
        {
            if (BattleChara == null) return 0.5f;
            var r = ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)BattleChara)->HitboxRadius;
            return r > 0.01f ? r : 0.5f;
        }
    }

    // Simulation state
    public SimulatedEntityState State { get; set; } = new();

    // AI
    public NpcBehavior Behavior { get; set; } = new();
    public Vector3 SpawnPosition { get; set; }
    public float AutoAttackTimer { get; set; }
    public float EngageDelayTimer { get; set; }
    /// <summary>Close enough to stand and fight (with hysteresis: see NpcAiController.TryCrowdApproach
    /// and CombatCompanionManager.Crowd).</summary>
    public bool InReach { get; set; }
    public ActorVisualState VisualState { get; set; } = new();

    // Casting
    public NpcSkill? CurrentCastSkill { get; set; }

    // Lifecycle
    public bool IsAlive => State.IsAlive;
    public bool IsSpawned { get; set; }

    // When true, every action this combatant takes is rendered with the ranged
    // attack motion (bow/gun draw + projectile) instead of the action's
    // default melee animation. Per-combatant override so e.g. an archer model
    // never plays a sword swing.
    public bool IsRanged { get; set; }

    /// <summary>The side it fights on. Read-only; nothing branches on it yet.</summary>
    public abstract CombatSide Side { get; }
}
