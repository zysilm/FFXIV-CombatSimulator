// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator.Battle;

/// <summary>What <see cref="KillerPossession{TNpc}"/> needs from the battle (the plugin's BattleModule, or a test's fake).</summary>
public interface IKillerPossessionWorld<TNpc> where TNpc : class
{
    /// <summary>Enemy Control's "On death: Control killer".</summary>
    bool ControlKillerEnabled { get; }
    /// <summary>The player (the lord) is alive: revived, or never down.</summary>
    bool LordAlive { get; }
    /// <summary>Enemy Control holds an enemy already (or a private director has taken over).</summary>
    bool IsControlling { get; }
    /// <summary>The lord's body has been carried off: the seizer is the one taken over (CorpseSeizure.SeizeBody).</summary>
    bool BodySeized { get; }
    /// <summary>The battle's outcome is decided and it is a defeat (CombatEvents.OutcomeDecided(false), the first word standing).</summary>
    bool BattleLost { get; }
    /// <summary>The enemy with this id in the battle, or null.</summary>
    TNpc? FindEnemy(uint entityId);
    /// <summary>
    /// Its game object is still there and still that enemy (the object table lists its address with the same
    /// entity id), and it is alive: a despawned enemy's address dangles and may already belong to another object.
    /// </summary>
    bool IsLiveAndSame(TNpc npc, uint entityId);
    void TakeControl(TNpc npc);
    void Info(string text);
}

/// <summary>
/// Enemy Control's "On death: Control killer", one rule for every battle (2026-11-15, the user's decision): Enemy Control
/// takes over an enemy only once the battle is lost. The lord's death alone decides nothing (allies may still win
/// it, or raise him): the killer is remembered by its entity id when the lord falls (<see cref="OnLordFell"/>) and,
/// in the war tick (<see cref="Tick"/>), taken over once the outcome is decided as a defeat. If the lord's body was
/// carried off, its seizer is the one taken over (CorpseSeizure.SeizeBody does it, right after the defeat), so the
/// killer is let go. The wait ends with nobody taken over when the lord is back on his feet, the option is turned
/// off, Enemy Control (or a director) holds someone already, or the killer's game object is gone, dead or no
/// longer that enemy (logged).
/// </summary>
public sealed class KillerPossession<TNpc> where TNpc : class
{
    private readonly IKillerPossessionWorld<TNpc> world;
    private uint pendingId;
    private bool waitLogged;

    public KillerPossession(IKillerPossessionWorld<TNpc> world) => this.world = world;

    /// <summary>A killer is waiting to be taken over.</summary>
    public bool Pending => pendingId != 0;

    /// <summary>The lord has fallen to the enemy with this id (0: none known).</summary>
    public void OnLordFell(uint killerEntityId)
    {
        if (!world.ControlKillerEnabled || killerEntityId == 0)
            return;
        pendingId = killerEntityId;
        waitLogged = false;
    }

    /// <summary>Forget the waiting killer (a reset, a director taking over).</summary>
    public void Cancel() => pendingId = 0;

    public void Tick()
    {
        if (pendingId == 0)
            return;
        if (!world.ControlKillerEnabled || world.LordAlive || world.IsControlling || world.BodySeized)
        {
            pendingId = 0;
            return;
        }
        if (!world.BattleLost)
        {
            if (!waitLogged)
                world.Info("EnemyControl: the lord is down but the battle is not lost yet; nobody is taken over until it is.");
            waitLogged = true;
            return;
        }

        var id = pendingId;
        pendingId = 0;
        var killer = world.FindEnemy(id);
        if (killer == null || !world.IsLiveAndSame(killer, id))
        {
            world.Info("EnemyControl: no valid killer to control (gone, dead, or its object reused).");
            return;
        }
        world.TakeControl(killer);
    }
}
