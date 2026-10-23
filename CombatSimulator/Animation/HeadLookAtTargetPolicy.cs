// This Source Code Form is subject to the Mozilla Public License, v. 2.0.
namespace CombatSimulator.Animation;

internal readonly record struct LookAtActor(nint Address, uint EntityId)
{
    public bool Valid => Address != 0 && EntityId != 0;
}

internal static class HeadLookAtTargetPolicy
{
    public static bool CanDrive(bool enemyAlive, bool otherControllerOwnsEnemy, bool playerDefeated)
        => enemyAlive && (playerDefeated || !otherControllerOwnsEnemy);

    public static LookAtActor? Select(LookAtActor enemy, LookAtActor combatTarget, LookAtActor player, bool playerDefeated)
    {
        var target = playerDefeated ? player : combatTarget;
        return enemy.Valid && target.Valid && target.Address != enemy.Address ? target : null;
    }
}
