// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Linq;
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
        MigrateVoiceDefaults();
        voiceDirector = new VoiceDirector(config, npcSelector, combatEngine,
            () => companionManager.HasLivingCompanions, dataManager, log);
        voiceDirector.DeadCompanionCount = () =>
            config.EnableCombatCompanions ? companionManager.Companions.Count(c => c.IsSpawned && !c.State.IsAlive) : 0;
        combatModeRouter.AttackCommitted += (npc, request) => voiceDirector.NotifyAttackCommitted(npc, request);
        combatEngine.OnSimulationReset += voiceDirector.Reset;
        // Stories speak through the same bubbles; enemy lines wait until the scene is over.
        voiceDirector.LinesSuppressed = () => PrivateDirectorActive;
        // Only a story's performer is held back, and only while the story runs, so it never talks
        // over its own lines. Enemy Control possesses the killer on every defeat; that actor is
        // often the last enemy standing and must still gloat. (ControlsNpc would also cover the
        // pack following it, which silenced the taunts entirely.)
        voiceDirector.IsActorReserved = addr => PrivateDirectorActive && enemyControlController.PossessesNpc(addr);
        mainWindow.VoiceDirector = voiceDirector;
    }

    /// <summary>
    /// Voices were experimental and off by default, so every existing config has "off" saved
    /// explicitly; the new on-by-default would only reach fresh installs. Switch them on once.
    /// </summary>
    private void MigrateVoiceDefaults()
    {
        if (config.EnemyVoiceDefaultsMigrated20261024)
            return;
        config.EnableEnemyVoices = true;
        config.EnemyVoiceBubbles = true;
        config.EnemyVoiceDefaultsMigrated20261024 = true;
        config.Save();
    }

    private void TickVoice(float deltaTime) => voiceDirector.Tick(deltaTime);
}
