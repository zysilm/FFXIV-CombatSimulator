// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using CombatSimulator.Encounters.Definitions;
using CombatSimulator.Recipes;

namespace CombatSimulator.Encounters.Runtime;

public interface IEncounterRuntime
{
    bool IsSimulationActive { get; }
    bool IsSpawnReady { get; }
    bool IsPlayerDead { get; }
    float? PlayerHpRatio { get; }
    int EnemyCount { get; }
    int LivingEnemyCount { get; }

    bool CanStart(out string reason);
    bool StartRecipe(string recipeName);
    void StopRecipe(bool print);
    int SpawnEnemies(EncounterCueDefinition cue, EncounterActorBinding? approachAnchor);
    int SpawnCompanions(IReadOnlyList<CombatRecipeCompanionGroup> companions);

    bool HasActor(EncounterActorBinding binding);
    nint GetActorAddress(EncounterActorBinding binding);
    float? GetActorHpRatio(EncounterActorBinding binding);
    bool IsActorDead(EncounterActorBinding binding);
    void ApplyEnemyPressure(EncounterActorBinding binding, EncounterCueDefinition cue);
    void ApplyPartyPower(EncounterCueDefinition cue);
    void ResetEncounterModifiers();
    void PlayPlayerVictory();
    void SubmitCamera(EncounterActorBinding binding, EncounterCueDefinition cue);
    void ReleaseCamera();
    void AddCombatLog(string message);
    void Print(string message);
    void PrintError(string message);
}
