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
    public ActorVisualState VisualState { get; set; } = new();

    // True when this enemy is engaged and its resolved combat target is the local
    // player. Updated each frame by NpcAiController; read by CombatLinkOverlay to
    // draw the red "attacking you" arcs.
    public bool IsAttackingPlayer { get; set; }

    // Casting
    public NpcSkill? CurrentCastSkill { get; set; }

    // Lifecycle
    public bool IsAlive => State.CurrentHp > 0;
    public bool IsSpawned { get; set; }

    // When true, every action this NPC takes is rendered with the ranged
    // attack motion (bow/gun draw + projectile) instead of the action's
    // default melee animation. Per-NPC override so e.g. an archer model
    // never plays a sword swing.
    public bool IsRanged { get; set; }

    // Action-Mode dynamic positioning: the engage distance + role the enemy wants right now, based
    // on the action it intends to use next. Updated each frame by NpcAiController; the approach
    // logic and PartyEngagePlanner read these instead of a fixed weapon-style range.
    // DesiredEngageRange == 0 means "not computed yet" (callers fall back to the style range).
    public float DesiredEngageRange { get; set; }
    public bool IsRangedIntent { get; set; }
    public float IntentDwellTimer { get; set; }
}
