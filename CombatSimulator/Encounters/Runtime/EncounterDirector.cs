// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using CombatSimulator.Encounters.Definitions;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Encounters.Runtime;

public enum EncounterRunState
{
    Inactive,
    Preparing,
    Running,
    Completed,
    Failed,
}

/// <summary>Small deterministic phase machine; all game-specific work stays behind IEncounterRuntime.</summary>
public sealed class EncounterDirector
{
    private readonly EncounterBook book;
    private readonly IEncounterRuntime runtime;
    private readonly IPluginLog log;
    private readonly HashSet<int> executedEnterCues = new();

    private EncounterDefinition? encounter;
    private EncounterPhaseDefinition? phase;
    private float stateElapsed;
    private float phaseElapsed;
    private bool sawEnemy;

    private EncounterActorBinding? cameraActor;
    private EncounterCueDefinition? cameraCue;
    private float cameraRemaining;
    private float presentationRemaining;

    public EncounterDirector(EncounterBook book, IEncounterRuntime runtime, IPluginLog log)
    {
        this.book = book;
        this.runtime = runtime;
        this.log = log;
    }

    public IReadOnlyList<EncounterDefinition> Encounters => book.Encounters;
    public EncounterRunState State { get; private set; }
    public bool IsActive => State is EncounterRunState.Preparing or EncounterRunState.Running;
    public string CurrentEncounterId => encounter?.Id ?? string.Empty;
    public string CurrentEncounterName => encounter?.Name ?? string.Empty;
    public string CurrentPhaseName => phase?.Name ?? (State == EncounterRunState.Preparing ? "Preparing" : string.Empty);
    public string PresentationTitle { get; private set; } = string.Empty;
    public string PresentationSpeaker { get; private set; } = string.Empty;
    public string PresentationText { get; private set; } = string.Empty;
    public bool HasPresentation => presentationRemaining > 0f &&
                                   (!string.IsNullOrWhiteSpace(PresentationTitle) ||
                                    !string.IsNullOrWhiteSpace(PresentationText));
    public nint CameraSubjectAddress => cameraActor == null
        ? nint.Zero
        : runtime.GetActorAddress(cameraActor);

    public bool TryStart(string encounterId)
    {
        var definition = book.Find(encounterId);
        if (definition == null)
        {
            runtime.PrintError($"Unknown encounter '{encounterId}'.");
            return false;
        }
        if (!runtime.CanStart(out var reason))
        {
            runtime.PrintError(reason);
            return false;
        }

        if (IsActive)
            Stop(stopCombat: true, print: false);

        ClearPresentation();
        encounter = definition;
        phase = null;
        State = EncounterRunState.Preparing;
        stateElapsed = 0f;
        phaseElapsed = 0f;
        sawEnemy = false;
        executedEnterCues.Clear();

        if (!runtime.StartRecipe(definition.Setup.Recipe))
        {
            State = EncounterRunState.Failed;
            encounter = null;
            return false;
        }

        runtime.Print($"Preparing '{definition.Name}'.");
        log.Info($"Encounter '{definition.Id}' preparing recipe '{definition.Setup.Recipe}'.");
        return true;
    }

    public bool Restart()
    {
        var id = CurrentEncounterId;
        if (string.IsNullOrWhiteSpace(id)) return false;
        Stop(stopCombat: true, print: false);
        return TryStart(id);
    }

    public void Tick(float deltaTime)
    {
        var dt = Math.Clamp(deltaTime, 0f, 0.1f);
        TickPresentation(dt);

        if (!IsActive)
            return;
        if (!runtime.IsSimulationActive)
        {
            Stop(stopCombat: false, print: false);
            return;
        }

        stateElapsed += dt;
        if (runtime.EnemyCount > 0)
            sawEnemy = true;

        if (State == EncounterRunState.Preparing)
        {
            if (runtime.IsSpawnReady && RequiredActorsReady())
            {
                EnterPhase(encounter!.InitialPhase);
                return;
            }

            if (stateElapsed >= Math.Max(1f, encounter!.Setup.ReadyTimeoutSeconds))
            {
                runtime.PrintError($"'{encounter.Name}' could not resolve its required actors in time.");
                Stop(stopCombat: true, print: false);
                State = EncounterRunState.Failed;
            }
            return;
        }

        phaseElapsed += dt;
        ExecuteScheduledCues();

        foreach (var transition in phase!.Transitions)
        {
            if (!ConditionsMet(transition.When)) continue;
            TransitionTo(transition.To);
            break; // Never traverse more than one phase in one frame.
        }
    }

    public void Stop(bool stopCombat = true, bool print = true)
    {
        var name = CurrentEncounterName;
        ClearPresentation();
        encounter = null;
        phase = null;
        executedEnterCues.Clear();
        stateElapsed = 0f;
        phaseElapsed = 0f;
        sawEnemy = false;
        State = EncounterRunState.Inactive;
        if (stopCombat)
            runtime.StopRecipe(print: false);
        if (print && !string.IsNullOrWhiteSpace(name))
            runtime.Print($"Stopped '{name}'.");
    }

    public void ResetWorld() => Stop(stopCombat: false, print: false);

    private bool RequiredActorsReady()
    {
        foreach (var binding in encounter!.Actors.Values)
            if (binding.Required && !runtime.HasActor(binding)) return false;
        return true;
    }

    private void EnterPhase(string phaseId)
    {
        ClearPhasePresentation();
        phase = FindPhase(phaseId);
        if (phase == null)
        {
            runtime.PrintError($"Encounter phase '{phaseId}' disappeared after validation.");
            Stop(stopCombat: true, print: false);
            State = EncounterRunState.Failed;
            return;
        }

        State = EncounterRunState.Running;
        stateElapsed = 0f;
        phaseElapsed = 0f;
        executedEnterCues.Clear();
        ExecuteScheduledCues();
        runtime.AddCombatLog($"Encounter phase: {phase.Name}");
        log.Info($"Encounter '{encounter!.Id}' entered phase '{phase.Id}'.");
    }

    private void ExecuteScheduledCues()
    {
        for (var i = 0; i < phase!.OnEnter.Count; i++)
        {
            if (executedEnterCues.Contains(i) || phase.OnEnter[i].AtSeconds > phaseElapsed)
                continue;
            executedEnterCues.Add(i);
            ExecuteCue(phase.OnEnter[i]);
        }
    }

    private void ExecuteCue(EncounterCueDefinition cue)
    {
        switch (cue.Type)
        {
            case EncounterCueType.Title:
                SetPresentation(cue.Text, string.Empty, string.Empty, cue.Duration);
                break;
            case EncounterCueType.Dialogue:
                SetPresentation(string.Empty, cue.Speaker, cue.Text, cue.Duration);
                if (!string.IsNullOrWhiteSpace(cue.Text))
                    runtime.Print(string.IsNullOrWhiteSpace(cue.Speaker)
                        ? cue.Text
                        : $"{cue.Speaker}: {cue.Text}");
                break;
            case EncounterCueType.CameraFocus:
                if (TryGetActor(cue.Actor, out var cameraBinding))
                {
                    cameraActor = cameraBinding;
                    cameraCue = cue;
                    cameraRemaining = Math.Max(0f, cue.Duration);
                }
                break;
            case EncounterCueType.SpawnEnemies:
                var count = runtime.SpawnEnemies(cue.Enemies);
                runtime.AddCombatLog($"Reinforcements incoming: {count}.");
                break;
            case EncounterCueType.EnemyPressure:
                if (TryGetActor(cue.Actor, out var pressureBinding))
                    runtime.ApplyEnemyPressure(pressureBinding, cue);
                break;
            case EncounterCueType.PlayerVictory:
                runtime.PlayPlayerVictory();
                break;
            case EncounterCueType.CombatLog:
                runtime.AddCombatLog(cue.Text);
                break;
        }
    }

    private bool ConditionsMet(IReadOnlyList<EncounterConditionDefinition> conditions)
    {
        foreach (var condition in conditions)
        {
            var met = condition.Type switch
            {
                EncounterConditionType.Elapsed => phaseElapsed >= Math.Max(0f, condition.Seconds),
                EncounterConditionType.ActorHpAtOrBelow =>
                    TryGetActor(condition.Actor, out var hpActor) &&
                    runtime.GetActorHpRatio(hpActor) is { } hp && hp <= condition.Ratio,
                EncounterConditionType.ActorDead =>
                    TryGetActor(condition.Actor, out var deadActor) && runtime.IsActorDead(deadActor),
                EncounterConditionType.PlayerDead => runtime.IsPlayerDead,
                EncounterConditionType.AllEnemiesDead =>
                    sawEnemy && !HasPendingSpawnCue() && runtime.IsSpawnReady && runtime.LivingEnemyCount == 0,
                _ => false,
            };
            if (!met) return false;
        }
        return true;
    }

    private bool HasPendingSpawnCue()
    {
        for (var i = 0; i < phase!.OnEnter.Count; i++)
            if (!executedEnterCues.Contains(i) && phase.OnEnter[i].Type == EncounterCueType.SpawnEnemies)
                return true;
        return false;
    }

    private void TransitionTo(string target)
    {
        foreach (var cue in phase!.OnExit)
            ExecuteCue(cue);

        if (target == "$complete")
        {
            ClearPhasePresentation();
            State = EncounterRunState.Completed;
            runtime.AddCombatLog($"Encounter complete: {encounter!.Name}");
            runtime.Print($"Completed '{encounter.Name}'.");
            phase = null;
            return;
        }
        if (target == "$failed")
        {
            ClearPhasePresentation();
            State = EncounterRunState.Failed;
            runtime.AddCombatLog($"Encounter failed: {encounter!.Name}");
            phase = null;
            return;
        }

        EnterPhase(target);
    }

    private EncounterPhaseDefinition? FindPhase(string id)
    {
        foreach (var candidate in encounter!.Phases)
            if (string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase)) return candidate;
        return null;
    }

    private bool TryGetActor(string alias, out EncounterActorBinding binding)
    {
        binding = null!;
        return encounter != null && encounter.Actors.TryGetValue(alias, out binding!);
    }

    private void TickPresentation(float dt)
    {
        if (presentationRemaining > 0f)
        {
            presentationRemaining -= dt;
            if (presentationRemaining <= 0f)
            {
                PresentationTitle = string.Empty;
                PresentationSpeaker = string.Empty;
                PresentationText = string.Empty;
            }
        }

        if (cameraRemaining > 0f && cameraActor != null && cameraCue != null)
        {
            cameraRemaining -= dt;
            runtime.SubmitCamera(cameraActor, cameraCue);
            if (cameraRemaining <= 0f)
            {
                runtime.ReleaseCamera();
                cameraActor = null;
                cameraCue = null;
            }
        }
    }

    private void SetPresentation(string title, string speaker, string text, float duration)
    {
        PresentationTitle = title;
        PresentationSpeaker = speaker;
        PresentationText = text;
        presentationRemaining = Math.Max(0.1f, duration);
    }

    private void ClearPhasePresentation()
    {
        runtime.ReleaseCamera();
        cameraRemaining = 0f;
        cameraActor = null;
        cameraCue = null;
    }

    private void ClearPresentation()
    {
        ClearPhasePresentation();
        presentationRemaining = 0f;
        PresentationTitle = string.Empty;
        PresentationSpeaker = string.Empty;
        PresentationText = string.Empty;
    }
}
