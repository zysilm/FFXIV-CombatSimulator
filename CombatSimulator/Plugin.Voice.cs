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
        // A story's performer is possessed through Enemy Control, as is a player-controlled enemy.
        // Only that one actor: ControlsNpc also covers the pack following it, which after a defeat
        // is every other enemy and silenced the taunts entirely.
        voiceDirector.IsActorReserved = addr => enemyControlController.PossessesNpc(addr);
        mainWindow.VoiceDirector = voiceDirector;
    }

    /// <summary>
    /// Voices were experimental and off by default, so every existing config has "off" saved
    /// explicitly; the new on-by-default would only reach fresh installs. Switch them on once.
    /// </summary>
    private void MigrateVoiceDefaults()
    {
        if (!config.EnemyVoiceCadenceMigrated20261025)
        {
            if (System.Math.Abs(config.EnemyVoiceTauntInterval - 25f) < 0.01f)
                config.EnemyVoiceTauntInterval = 14f;
            config.EnemyVoiceCadenceMigrated20261025 = true;
            config.Save();
        }

        if (config.EnemyVoiceDefaultsMigrated20261024)
            return;
        config.EnableEnemyVoices = true;
        config.EnemyVoiceLines = true;
        config.EnemyVoiceBubbles = true;
        config.EnemyVoiceDefaultsMigrated20261024 = true;
        config.Save();
    }

    private void TickVoice(float deltaTime) => voiceDirector.Tick(deltaTime);
}
