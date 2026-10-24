// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Voice;
using Dalamud.Plugin.Services;

namespace CombatSimulator;

// Enemy voice wiring lives here so the main constructor only gains one call per lifecycle stage.
public sealed partial class CombatSimulatorPlugin
{
    private VoiceDirector voiceDirector = null!;

    /// <summary>Construct and subscribe. Subscriptions are additive (+=) and registered after the
    /// constructor's own assignments, so they never replace another listener.</summary>
    private void InitVoice(IDataManager dataManager)
    {
        voiceDirector = new VoiceDirector(config, npcSelector, combatEngine,
            () => companionManager.HasLivingCompanions, dataManager, log);
        combatModeRouter.AttackCommitted += (npc, request) => voiceDirector.NotifyAttackCommitted(npc, request);
        combatEngine.OnPlayerDeath += _ => voiceDirector.NotifyPlayerDeath();
        combatEngine.OnSimulationReset += voiceDirector.Reset;
        mainWindow.VoiceDirector = voiceDirector;
    }

    private void TickVoice(float deltaTime) => voiceDirector.Tick(deltaTime);
}
