// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using CombatSimulator.Encounters.Definitions;
using CombatSimulator.Encounters.Runtime;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Encounters;

/// <summary>
/// Detects the falling edge of real cutscene flags and offers an opt-in local encounter after the
/// client has been idle and stable. It never changes quest state and never starts automatically.
/// </summary>
public sealed class StoryEncounterPromptController
{
    private const float MinimumSceneSeconds = 1f;
    private const float SettleSeconds = 1.25f;
    private const float SettleTimeoutSeconds = 20f;
    private const float PromptSeconds = 20f;
    private const float OfferCooldownSeconds = 30f;

    private readonly Configuration config;
    private readonly EncounterBook book;
    private readonly EncounterDirector director;
    private readonly IEncounterRuntime runtime;
    private readonly IClientState clientState;
    private readonly ICondition condition;

    private bool wasInScene;
    private float sceneElapsed;
    private float settleElapsed;
    private float settleWindowElapsed;
    private float promptRemaining;
    private float cooldownRemaining;
    private uint pendingTerritory;

    public StoryEncounterPromptController(
        Configuration config,
        EncounterBook book,
        EncounterDirector director,
        IEncounterRuntime runtime,
        IClientState clientState,
        ICondition condition)
    {
        this.config = config;
        this.book = book;
        this.director = director;
        this.runtime = runtime;
        this.clientState = clientState;
        this.condition = condition;
        wasInScene = IsStorySceneActive();
    }

    public EncounterDefinition? PendingEncounter { get; private set; }
    public bool IsPromptVisible => PendingEncounter != null && promptRemaining > 0f;

    public void Tick(float deltaTime)
    {
        var dt = Math.Clamp(deltaTime, 0f, 0.1f);
        cooldownRemaining = Math.Max(0f, cooldownRemaining - dt);

        if (!config.EnablePostCutsceneEncounterPrompt)
        {
            ResetCandidate();
            wasInScene = IsStorySceneActive();
            return;
        }

        var inScene = IsStorySceneActive();
        if (inScene)
        {
            if (!wasInScene) sceneElapsed = 0f;
            sceneElapsed += dt;
            ResetCandidate();
            wasInScene = true;
            return;
        }

        if (wasInScene)
        {
            wasInScene = false;
            if (sceneElapsed >= MinimumSceneSeconds && cooldownRemaining <= 0f)
            {
                settleElapsed = 0f;
                settleWindowElapsed = 0f;
                pendingTerritory = clientState.TerritoryType;
            }
            sceneElapsed = 0f;
        }

        if (IsPromptVisible)
        {
            if (!CanOffer() || pendingTerritory != clientState.TerritoryType)
            {
                Decline();
                return;
            }
            promptRemaining -= dt;
            if (promptRemaining <= 0f) Decline();
            return;
        }

        if (pendingTerritory == 0)
            return;

        settleWindowElapsed += dt;
        if (settleWindowElapsed > SettleTimeoutSeconds)
        {
            ResetCandidate();
            return;
        }

        if (!CanOffer())
        {
            settleElapsed = 0f;
            return;
        }

        settleElapsed += dt;
        if (settleElapsed < SettleSeconds)
            return;

        PendingEncounter = ResolveStoryEncounter();
        if (PendingEncounter == null)
        {
            ResetCandidate();
            return;
        }
        promptRemaining = PromptSeconds;
    }

    public bool Accept()
    {
        var pending = PendingEncounter;
        if (pending == null || pendingTerritory != clientState.TerritoryType || !CanOffer())
        {
            Decline();
            return false;
        }

        ResetCandidate();
        cooldownRemaining = OfferCooldownSeconds;
        return director.TryStart(pending.Id);
    }

    public void Decline()
    {
        ResetCandidate();
        cooldownRemaining = OfferCooldownSeconds;
    }

    public void Reset()
    {
        ResetCandidate();
        sceneElapsed = 0f;
        cooldownRemaining = 0f;
        wasInScene = IsStorySceneActive();
    }

    private EncounterDefinition? ResolveStoryEncounter()
    {
        if (!string.IsNullOrWhiteSpace(config.PostCutsceneEncounterId))
        {
            var selected = book.Find(config.PostCutsceneEncounterId);
            if (selected?.StoryEligible == true) return selected;
        }

        foreach (var encounter in book.Encounters)
            if (encounter.StoryEligible) return encounter;
        return null;
    }

    private bool CanOffer()
    {
        if (!clientState.IsLoggedIn || clientState.IsPvP || clientState.IsGPosing)
            return false;
        if (Core.Services.ObjectTable.LocalPlayer is not { Address: not 0 })
            return false;
        if (director.IsActive || runtime.IsSimulationActive || IsStorySceneActive())
            return false;
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ||
            condition[ConditionFlag.LoggingOut] || condition[ConditionFlag.InCombat] ||
            condition[ConditionFlag.Unconscious] || condition[ConditionFlag.Occupied] ||
            condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent] ||
            condition[ConditionFlag.DutyRecorderPlayback])
            return false;
        if (!clientState.IsClientIdle(out _))
            return false;
        return runtime.CanStart(out _);
    }

    private bool IsStorySceneActive() =>
        condition[ConditionFlag.WatchingCutscene] ||
        condition[ConditionFlag.WatchingCutscene78] ||
        condition[ConditionFlag.OccupiedInCutSceneEvent];

    private void ResetCandidate()
    {
        PendingEncounter = null;
        pendingTerritory = 0;
        settleElapsed = 0f;
        settleWindowElapsed = 0f;
        promptRemaining = 0f;
    }
}
