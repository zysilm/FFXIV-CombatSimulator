using System;
using System.Collections.Generic;

namespace CombatSimulator.ActionCombat;

/// <summary>Logical role a hotbar press maps to in Action Mode.</summary>
public enum PlayerInputRole
{
    LightAttack,
    Dodge,
    Skill1,
    Skill2,
}

public readonly record struct PlayerActionInput(PlayerInputRole Role, uint ActionId);

/// <summary>
/// The Action-Mode player input sink. <see cref="UseActionHook"/> routes every
/// intercepted combat action here while <c>config.ActionMode</c> is on; the real
/// action never fires. The pressed <c>actionId</c> is mapped to a role via the
/// configured map and queued for the framework thread to drain.
/// </summary>
public interface IPlayerActionSink
{
    void OnAction(uint actionType, uint actionId, ulong targetId, uint extraParam);
}

public sealed class ActionComboSink : IPlayerActionSink
{
    private readonly Configuration config;
    private readonly Queue<PlayerActionInput> queue = new();
    private readonly object gate = new();

    public ActionComboSink(Configuration config) => this.config = config;

    public void OnAction(uint actionType, uint actionId, ulong targetId, uint extraParam)
    {
        var input = new PlayerActionInput(MapRole(actionId), actionId);
        lock (gate)
            queue.Enqueue(input);
    }

    // Dodge / skills are opt-in by actionId; anything else is a light attack, so the
    // mode is usable before the player configures specific ids.
    private PlayerInputRole MapRole(uint id)
    {
        if (config.ActionDodgeId != 0 && id == config.ActionDodgeId) return PlayerInputRole.Dodge;
        if (config.ActionSkill1Id != 0 && id == config.ActionSkill1Id) return PlayerInputRole.Skill1;
        if (config.ActionSkill2Id != 0 && id == config.ActionSkill2Id) return PlayerInputRole.Skill2;
        return PlayerInputRole.LightAttack;
    }

    public void Drain(Action<PlayerActionInput> handle)
    {
        while (true)
        {
            PlayerActionInput input;
            lock (gate)
            {
                if (queue.Count == 0)
                    return;
                input = queue.Dequeue();
            }
            handle(input);
        }
    }

    public void Clear()
    {
        lock (gate)
            queue.Clear();
    }
}
