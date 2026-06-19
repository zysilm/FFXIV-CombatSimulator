namespace CombatSimulator.ActionCombat;

/// <summary>
/// Selects the active enemy-attack executor by mode, so call sites (NpcAiController)
/// stay mode-agnostic: <c>router.AttackExecutor.Execute(...)</c>. Off = behavior-
/// equivalent instant path; on = telegraphed.
/// </summary>
public sealed class CombatModeRouter
{
    private readonly Configuration config;
    private readonly IAttackExecutor instant;
    private readonly IAttackExecutor telegraphed;

    public CombatModeRouter(Configuration config, IAttackExecutor instant, IAttackExecutor telegraphed)
    {
        this.config = config;
        this.instant = instant;
        this.telegraphed = telegraphed;
    }

    public IAttackExecutor AttackExecutor => config.ActionMode ? telegraphed : instant;
}
