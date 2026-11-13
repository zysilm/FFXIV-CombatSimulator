// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using CombatSimulator.Ai;
using CombatSimulator.Animation;
using CombatSimulator.Simulation;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace CombatSimulator.Npcs;

public unsafe class SimulatedNpc
{
    // Identity
    public uint SimulatedEntityId { get; set; }
    public int ObjectIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint BNpcBaseId { get; set; }
    public uint BNpcNameId { get; set; }
    public uint ENpcBaseId { get; set; }

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

    // Control mode (false = real game NPC, true = client-spawned)
    public bool IsClientControlled { get; set; }

    // Original state (for restore on deselect)
    public int OriginalModelCharaId { get; set; }
    public bool ModelChanged { get; set; }
    public byte OriginalObjectKind { get; set; }
    public byte OriginalSubKind { get; set; }

    // Simulation state
    public SimulatedEntityState State { get; set; } = new();

    // AI
    public NpcAiState AiState { get; set; } = NpcAiState.Idle;
    public NpcBehavior Behavior { get; set; } = new();
    public Vector3 SpawnPosition { get; set; }
    public float AutoAttackTimer { get; set; }
    public float EngageDelayTimer { get; set; }
    public float DeadTimer { get; set; }
    public bool IsEngaged => AiState is NpcAiState.Engaging or NpcAiState.Combat or NpcAiState.Chasing;

    /// <summary>
    /// Holds its post while idle instead of joining the fight with everyone else: it engages when
    /// the party comes within its sight, when a neighbour does, or when it is hit.
    /// </summary>
    public bool IsSentry { get; set; }
    /// <summary>
    /// Moved this frame by the crowd on the plugin's navigation mesh (docs/AI_MOVEMENT_REWRITE.md):
    /// its range tests then use the same reach and the same gap as its movement.
    /// </summary>
    public bool CrowdDriven { get; set; }
    /// <summary>Close enough to stand and fight (with hysteresis: see NpcAiController.TryCrowdApproach).</summary>
    public bool InReach { get; set; }
    /// <summary>Moving at a walk (a patrol), not a run.</summary>
    public bool WalkPace { get; set; }
    /// <summary>Where this enemy is going to carry off the fallen lord's body (Warring States), or null.</summary>
    public Vector3? SeizeGoal { get; set; }
    /// <summary>A sentry that walks about its post while idle.</summary>
    public bool Patrols { get; set; }
    public Vector3 PatrolGoal { get; set; }
    public bool HasPatrolGoal { get; set; }
    /// <summary>Seconds left standing at the patrol point reached; then, seconds allowed to reach the next.</summary>
    public float PatrolTimer { get; set; }
    public ActorVisualState VisualState { get; set; } = new();

    // True when this enemy is engaged and its resolved combat target is the local
    // player. Updated each frame by NpcAiController; read by CombatLinkOverlay to
    // draw the red "attacking you" arcs.
    public bool IsAttackingPlayer { get; set; }

    // Casting
    public NpcSkill? CurrentCastSkill { get; set; }
    /// <summary>Area snapshotted when a telegraphed cast began; the cast resolves against it.</summary>
    public CombatSimulator.Simulation.TelegraphShape? CastArea { get; set; }

    // Post-spawn placement enforcement: frames left during which the clone is pinned to
    // SpawnPosition. Something inside the game keeps relocating a freshly cloned humanoid onto the
    // player despite the spawn placement writes (masked historically by the approach system walking
    // it away); rather than chase the writer, hold the clone at its intended spot for the first
    // ~half second after draw, then hand control to the AI.
    public int SpawnPlacementFrames { get; set; }

    // Lifecycle
    public bool IsAlive => State.CurrentHp > 0;
    public bool IsSpawned { get; set; }

    // When true, every action this NPC takes is rendered with the ranged
    // attack motion (bow/gun draw + projectile) instead of the action's
    // default melee animation. Per-NPC override so e.g. an archer model
    // never plays a sword swing.
    public bool IsRanged { get; set; }
}
