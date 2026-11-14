// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using CombatSimulator.Ai;
using CombatSimulator.Combat;

namespace CombatSimulator.Npcs;

/// <summary>An enemy. What it shares with an ally is on <see cref="Combatant"/>.</summary>
public unsafe class SimulatedNpc : Combatant
{
    public override CombatSide Side => CombatSide.Enemy;

    // Identity
    public uint BNpcBaseId { get; set; }
    public uint BNpcNameId { get; set; }
    public uint ENpcBaseId { get; set; }

    // Control mode (false = real game NPC, true = client-spawned)
    public bool IsClientControlled { get; set; }

    // Original state (for restore on deselect)
    public int OriginalModelCharaId { get; set; }
    public bool ModelChanged { get; set; }
    public byte OriginalObjectKind { get; set; }
    public byte OriginalSubKind { get; set; }

    // AI
    public NpcAiState AiState { get; set; } = NpcAiState.Idle;
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
    /// <summary>Moving at a walk (a patrol), not a run.</summary>
    public bool WalkPace { get; set; }
    /// <summary>Where this enemy is going to carry off the fallen lord's body (Warring States), or null.</summary>
    public Vector3? SeizeGoal { get; set; }
    /// <summary>Going for the lord's body while a retainer stands over it: it fights its way in.</summary>
    public bool SeizeHeld { get; set; }
    /// <summary>A sentry that walks about its post while idle.</summary>
    public bool Patrols { get; set; }
    public Vector3 PatrolGoal { get; set; }
    public bool HasPatrolGoal { get; set; }
    /// <summary>Seconds left standing at the patrol point reached; then, seconds allowed to reach the next.</summary>
    public float PatrolTimer { get; set; }

    // True when this enemy is engaged and its resolved combat target is the local
    // player. Updated each frame by NpcAiController; read by CombatLinkOverlay to
    // draw the red "attacking you" arcs.
    public bool IsAttackingPlayer { get; set; }

    // Casting
    /// <summary>Area snapshotted when a telegraphed cast began; the cast resolves against it.</summary>
    public CombatSimulator.Simulation.TelegraphShape? CastArea { get; set; }

    // Post-spawn placement enforcement: frames left during which the clone is pinned to
    // SpawnPosition. Something inside the game keeps relocating a freshly cloned humanoid onto the
    // player despite the spawn placement writes (masked historically by the approach system walking
    // it away); rather than chase the writer, hold the clone at its intended spot for the first
    // ~half second after draw, then hand control to the AI.
    public int SpawnPlacementFrames { get; set; }
}
