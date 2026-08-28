// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using CombatSimulator.Recipes;

namespace CombatSimulator.Encounters.Definitions;

public sealed class EncounterDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool StoryEligible { get; set; }
    public EncounterSetupDefinition Setup { get; set; } = new();
    public Dictionary<string, EncounterActorBinding> Actors { get; set; } = new();
    public string InitialPhase { get; set; } = string.Empty;
    public List<EncounterPhaseDefinition> Phases { get; set; } = new();
}

public sealed class EncounterSetupDefinition
{
    public string Recipe { get; set; } = string.Empty;
    public float ReadyTimeoutSeconds { get; set; } = 8f;
}

public sealed class EncounterActorBinding
{
    public string Name { get; set; } = string.Empty;
    public int Occurrence { get; set; } = 1;
    public bool Required { get; set; } = true;
}

public sealed class EncounterPhaseDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<EncounterCueDefinition> OnEnter { get; set; } = new();
    public List<EncounterCueDefinition> OnExit { get; set; } = new();
    public List<EncounterTransitionDefinition> Transitions { get; set; } = new();
}

public enum EncounterCueType
{
    Title,
    Dialogue,
    CameraFocus,
    SpawnEnemies,
    EnemyPressure,
    PlayerVictory,
    CombatLog,
}

public sealed class EncounterCueDefinition
{
    public EncounterCueType Type { get; set; }
    public float AtSeconds { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Speaker { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public float Duration { get; set; } = 2.5f;
    public float Distance { get; set; } = 5f;
    public float HeightOffset { get; set; } = 1.2f;
    public float FovDegrees { get; set; } = 48f;
    public float AutoAttackDelayMultiplier { get; set; } = 1f;
    public float MoveSpeedMultiplier { get; set; } = 1f;
    public float DamageTakenMultiplier { get; set; } = 1f;
    public List<CombatRecipeEnemyGroup> Enemies { get; set; } = new();
}

public sealed class EncounterTransitionDefinition
{
    public List<EncounterConditionDefinition> When { get; set; } = new();
    public string To { get; set; } = string.Empty;
}

public enum EncounterConditionType
{
    Elapsed,
    ActorHpAtOrBelow,
    ActorDead,
    PlayerDead,
    AllEnemiesDead,
}

public sealed class EncounterConditionDefinition
{
    public EncounterConditionType Type { get; set; }
    public string Actor { get; set; } = string.Empty;
    public float Seconds { get; set; }
    public float Ratio { get; set; }
}
