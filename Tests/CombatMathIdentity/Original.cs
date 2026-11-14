using System.Numerics;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

// Verbatim copies of the expressions B1 moved into CombatantMath / AggroModel, taken from
// `git show f3568b9:<path>` (the commit before the move). Only the surroundings are adapted: a game
// pointer read becomes a parameter, `config` is a plain object with the same property names, and
// `combatEngine.GetActionData(id)?.MpCost ?? 0` becomes `priceOf(id)`. The arithmetic is untouched.

/// <summary>The settings the copies read, under the names Configuration uses.</summary>
sealed class OrigConfig
{
    public bool EnableNpcScale;
    public float NpcScale;
    public float NpcAttackRangeScale;
    public bool ActionMode;
    public float ActionEnemyAttackSpeed;
    public bool EnhancedEnemyTactics;
}

/// <summary>CombatSimulator/Ai/NpcAiController.cs and NpcAiController.Tactics.cs at f3568b9.</summary>
static class OrigEnemy
{
    // NpcAiController.cs:38
    private const float MagicAutoAttackMeleeRange = 3.5f;
    // NpcAiController.cs:471-474
    private const float MeleeStandGap = 1.0f;
    private const float RangedStandMargin = 1.0f;
    private const float StopTolerance = 0.25f;
    private const float MeleeReachLimit = 5f;
    // NpcAiController.Tactics.cs:100
    private const float BaseTurnRate = 5.0f;

    // NpcAiController.cs:480-481 (EntityHitboxRadius(target) -> targetRadius, npc.HitboxRadius -> npcRadius)
    public static float CrowdGap(float npcRadius, float targetRadius, Vector3 npcPos, Vector3 targetPos)
        => MathF.Max(0f, FlatDistance(npcPos, targetPos) - npcRadius - targetRadius);

    // NpcAiController.cs:487-505
    public static float ReachNow(OrigConfig config, NpcBehavior behavior, SimulatedEntityState state, Func<uint, int> priceOf)
    {
        var reach = behavior.AutoAttackStyle switch
        {
            NpcAttackStyle.Ranged => ScaleNpcNonMeleeRange(config, behavior.AutoAttackRange),
            NpcAttackStyle.Magic => ScaleNpcMeleeRange(config, MagicAutoAttackMeleeRange),
            _ => ScaleNpcMeleeRange(config, behavior.AutoAttackRange),
        };
        var hpShare = state.MaxHp > 0 ? (float)state.CurrentHp / state.MaxHp : 1f;
        foreach (var skill in behavior.Skills)
        {
            if (skill.CooldownRemaining > 0 || hpShare > skill.HpThreshold)
                continue;
            if (state.CurrentMp < SkillMpCost(state, skill.ActionId, priceOf))
                continue;
            reach = MathF.Max(reach, SkillReach(config, behavior, skill));
        }
        return reach;
    }

    // NpcAiController.cs:517 (TryCrowdMove)
    public static float Speed(NpcBehavior behavior)
        => behavior.MoveSpeed > 0 ? behavior.MoveSpeed * 1.5f : 8.0f;

    // NpcAiController.cs:547-549
    public static float Stand(float reach)
        => reach <= MeleeReachLimit
            ? MathF.Min(MeleeStandGap, reach * 0.5f)
            : MathF.Max(MeleeStandGap, reach - RangedStandMargin);

    // NpcAiController.cs:551-554
    public static bool InReach(bool inReach, float gap, float reach, float stand)
    {
        if (inReach && gap > reach)
            inReach = false;
        else if (!inReach && gap <= stand + StopTolerance)
            inReach = true;
        return inReach;
    }

    // NpcAiController.cs:556 and 564-566
    public static Vector3 Goal(float npcRadius, float targetRadius, Vector3 npcPos, Vector3 targetPos, float stand)
    {
        var bodies = npcRadius + targetRadius;
        var away = new Vector3(npcPos.X - targetPos.X, 0f, npcPos.Z - targetPos.Z);
        away = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : Vector3.UnitZ;
        return targetPos + away * (stand + bodies);
    }

    // NpcAiController.cs:723
    public static float ActionPace(OrigConfig config) => config.ActionMode ? MathF.Max(0.1f, config.ActionEnemyAttackSpeed) : 1f;

    // NpcAiController.cs:886-887
    public static bool HoldsMagicSwing(OrigConfig config, float distToTarget)
        => distToTarget > ScaleNpcMeleeRange(config, MagicAutoAttackMeleeRange);

    // NpcAiController.cs:911-914
    public static int SkillMpCost(SimulatedEntityState state, uint actionId, Func<uint, int> priceOf)
    {
        return CombatBudgetModel.EnemySkillMpCost(priceOf(actionId), state.MaxMp);
    }

    // NpcAiController.cs:1080-1085
    public static float FlatDistance(Vector3 a, Vector3 b)
    {
        var delta = a - b;
        delta.Y = 0;
        return delta.Length();
    }

    // NpcAiController.cs:1093-1119
    private static float NpcMeleeRangeScale(OrigConfig config)
    {
        var visualScale = config.EnableNpcScale
            ? Math.Clamp(config.NpcScale, 0.01f, 3.0f)
            : 1.0f;
        return visualScale * NpcAttackRangeScale(config);
    }

    private static float NpcAttackRangeScale(OrigConfig config)
        => Math.Clamp(config.NpcAttackRangeScale, 0.1f, 100f);

    public static float ScaleNpcNonMeleeRange(OrigConfig config, float range)
        => MathF.Max(0.001f, MathF.Max(0f, range) * NpcAttackRangeScale(config));

    public static float ScaleNpcMeleeRange(OrigConfig config, float range)
        => MathF.Max(0.001f, MathF.Max(0f, range) * NpcMeleeRangeScale(config));

    public static float SkillReach(OrigConfig config, NpcBehavior behavior, NpcSkill skill)
    {
        var style = skill.AttackStyle == NpcAttackStyle.Auto
            ? behavior.AutoAttackStyle
            : skill.AttackStyle;
        return style == NpcAttackStyle.Melee
            ? ScaleNpcMeleeRange(config, skill.Range)
            : ScaleNpcNonMeleeRange(config, skill.Range);
    }

    // NpcAiController.Tactics.cs:478-481
    public static float NpcTurnRate(OrigConfig config, float hitboxRadius)
        => config.EnhancedEnemyTactics
            ? EnemyMovementMath.TurnRate(BaseTurnRate, hitboxRadius)
            : BaseTurnRate;

    // NpcAiController.cs:1169-1193 (gameObj->Position / Rotation -> parameters; the write -> return)
    public static bool ForceRotateToward(OrigConfig config, float hitboxRadius, Vector3 npcPos, float rotation, Vector3 targetPos, float deltaTime, out float newRot)
    {
        newRot = float.NaN;
        var dir = targetPos - npcPos;

        if (dir.LengthSquared() < 0.001f) return false;

        var targetRot = MathF.Atan2(dir.X, dir.Z);
        var currentRot = rotation;
        var diff = targetRot - currentRot;

        while (diff > MathF.PI) diff -= 2 * MathF.PI;
        while (diff < -MathF.PI) diff += 2 * MathF.PI;

        var rotSpeed = NpcTurnRate(config, hitboxRadius) * deltaTime;
        if (MathF.Abs(diff) < rotSpeed)
            newRot = targetRot;
        else
            newRot = currentRot + MathF.Sign(diff) * rotSpeed;
        return true;
    }
}

/// <summary>CombatSimulator/Companions/CombatCompanionManager*.cs at f3568b9.</summary>
static class OrigAlly
{
    // CombatCompanionManager.Crowd.cs:29-33
    private const float CrowdMeleeStandGap = 1.0f;
    private const float CrowdRangedStandMargin = 1.0f;
    private const float CrowdStopTolerance = 0.25f;
    private const float CrowdMeleeReachLimit = 5f;
    public const float CrowdMagicAutoReach = 3.5f;

    // CombatCompanionManager.Crowd.cs:69-74 (HitboxOf(self) -> selfRadius, target.HitboxRadius -> targetRadius)
    public static float CrowdGap(Vector3 self, Vector3 other, float selfRadius, float targetRadius)
        => MathF.Max(0f, FlatDistance(self, other) - selfRadius - targetRadius);

    // CombatCompanionManager.Crowd.cs:76
    public static float SkillReach(NpcSkill skill) => skill.Range;

    // CombatCompanionManager.Crowd.cs:82-100
    public static float CrowdReachNow(NpcBehavior behavior, SimulatedEntityState state, Func<uint, int> priceOf)
    {
        var reach = behavior.AutoAttackStyle switch
        {
            NpcAttackStyle.Ranged => behavior.AutoAttackRange,
            NpcAttackStyle.Magic => CrowdMagicAutoReach,
            _ => MathF.Max(behavior.AutoAttackRange, 3f),
        };
        foreach (var skill in behavior.Skills)
        {
            if (skill.CooldownRemaining > 0)
                continue;
            var mpCost = CombatBudgetModel.EnemySkillMpCost(priceOf(skill.ActionId), state.MaxMp);
            if (state.CurrentMp < mpCost)
                continue;
            reach = MathF.Max(reach, SkillReach(skill));
        }
        return reach;
    }

    // CombatCompanionManager.Crowd.cs:110-112
    public static float Stand(float reach)
        => reach <= CrowdMeleeReachLimit
            ? MathF.Min(CrowdMeleeStandGap, reach * 0.5f)
            : MathF.Max(CrowdMeleeStandGap, reach - CrowdRangedStandMargin);

    // CombatCompanionManager.Crowd.cs:114-117
    public static bool InReach(bool inReach, float gap, float reach, float stand)
    {
        if (inReach && gap > reach)
            inReach = false;
        else if (!inReach && gap <= stand + CrowdStopTolerance)
            inReach = true;
        return inReach;
    }

    // CombatCompanionManager.Crowd.cs:121-125
    public static Vector3 Goal(Vector3 self, Vector3 targetPos, float stand, float selfRadius, float targetRadius)
    {
        var away = new Vector3(self.X - targetPos.X, 0f, self.Z - targetPos.Z);
        away = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : Vector3.UnitZ;
        return targetPos + away * (stand + selfRadius + targetRadius);
    }

    // CombatCompanionManager.Crowd.cs:141
    public static float Speed(NpcBehavior behavior)
        => behavior.MoveSpeed > 0 ? behavior.MoveSpeed : 6f;

    // CombatCompanionManager.cs:967 (the skill's MP cost in TickCompanion)
    public static int SkillMpCost(SimulatedEntityState state, uint actionId, Func<uint, int> priceOf)
        => CombatBudgetModel.EnemySkillMpCost(priceOf(actionId), state.MaxMp);

    // CombatCompanionManager.cs:988
    public static bool HoldsMagicSwing(float dist) => dist > CrowdMagicAutoReach;

    // CombatCompanionManager.cs:1008
    public static float ActionPace(OrigConfig config) => config.ActionMode ? Math.Max(0.1f, config.ActionEnemyAttackSpeed) : 1f;

    // CombatCompanionManager.cs:1136-1141
    public static float FlatDistance(Vector3 a, Vector3 b)
    {
        var delta = a - b;
        delta.Y = 0;
        return delta.Length();
    }

    // CombatCompanionManager.cs:1387-1403 (obj->Position / Rotation -> parameters; the write -> return)
    public static bool RotateToward(Vector3 pos, float rotation, Vector3 targetPos, float deltaTime, out float nextRot)
    {
        nextRot = float.NaN;
        var dir = targetPos - pos;
        if (dir.LengthSquared() < 0.001f)
            return false;

        var targetRot = MathF.Atan2(dir.X, dir.Z);
        var currentRot = rotation;
        var diff = targetRot - currentRot;
        while (diff > MathF.PI) diff -= 2 * MathF.PI;
        while (diff < -MathF.PI) diff += 2 * MathF.PI;

        var rotStep = 6.0f * deltaTime;
        nextRot = MathF.Abs(diff) < rotStep ? targetRot : currentRot + MathF.Sign(diff) * rotStep;
        return true;
    }

    // CombatCompanionManager.cs:460-461
    public static bool IsRangedStyle(NpcAttackStyle style)
        => style is NpcAttackStyle.Ranged or NpcAttackStyle.Magic;
}

/// <summary>CombatSimulator/Companions/CombatCompanionManager.Aggro.cs at f3568b9.</summary>
static class OrigAggro
{
    // .Aggro.cs:27-37
    private const float AggroDwellMin = 4f;
    private const float AggroDwellMax = 8f;
    private const float AggroHitHold = 2.5f;
    private const float NewAttackerWindow = 5f;
    private const float ThreatFloor = 0.25f;
    private const float ThreatGain = 1.5f;
    private const float CrowdK = 4f;
    private const float AggroDistanceScale = 10f;
    private const float FinishGain = 0.5f;
    private const float MutualGain = 1.5f;
    private const float StickyGain = 6f;

    public sealed class AggroState
    {
        public uint Target;
        public float HeldSince;
        public float DwellUntil;
        public bool HitByNew;
        public uint Focus;
        public uint Holder;
        public Random Rng = null!;
        public readonly Dictionary<uint, float> LastHitBy = new();
    }

    // .Aggro.cs:114-121 (combatEngine.State.SimulationTime -> now)
    public static void NoteHit(AggroState state, uint sourceId, float now)
    {
        if (now - state.LastHitBy.GetValueOrDefault(sourceId, float.NegativeInfinity) > NewAttackerWindow && state.Target != sourceId)
            state.HitByNew = true;
        state.LastHitBy[sourceId] = now;
    }

    // .Aggro.cs:146-173 (threatByEnemy and aggroCounts -> parameters)
    public static float AggroWeight(
        Dictionary<uint, Dictionary<uint, float>> threatByEnemy, Dictionary<uint, (int Melee, int Ranged)> aggroCounts,
        uint selfId, Vector3 selfPos, float selfReach, bool selfRanged, uint currentTarget,
        uint candidateId, Vector3 candidatePos, float candidateHpShare, uint candidateTarget)
    {
        var share = 0f;
        if (threatByEnemy.TryGetValue(selfId, out var table) && table.Count > 0)
        {
            var max = 0f;
            foreach (var v in table.Values)
                max = MathF.Max(max, v);
            share = EnemyMovementMath.ThreatShare(table.GetValueOrDefault(candidateId), max);
        }
        var w = ThreatFloor + ThreatGain * share * share;

        var (melee, ranged) = aggroCounts.GetValueOrDefault(candidateId);
        var same = (selfRanged ? ranged : melee) - (currentTarget == candidateId ? 1 : 0);
        var other = selfRanged ? melee : ranged;
        var crowd = MathF.Max(0f, same) + 0.5f * other;
        w /= 1f + crowd * crowd / (CrowdK * CrowdK);

        w /= 1f + MathF.Max(0f, OrigAlly.FlatDistance(selfPos, candidatePos) - selfReach) / AggroDistanceScale;
        w *= 1f + FinishGain * (1f - Math.Clamp(candidateHpShare, 0f, 1f));
        if (candidateTarget == selfId)
            w *= MutualGain;
        if (candidateId == currentTarget)
            w *= StickyGain;
        return w;
    }

    // .Aggro.cs:175-190
    public static uint DrawAggro(AggroState state, List<(uint Id, float Weight)> aggroDraw)
    {
        var total = 0.0;
        foreach (var (_, w) in aggroDraw)
            total += w;
        if (aggroDraw.Count == 0)
            return 0;
        var r = state.Rng.NextDouble() * total;
        foreach (var (id, w) in aggroDraw)
        {
            r -= w;
            if (r <= 0)
                return id;
        }
        return aggroDraw[^1].Id;
    }

    // .Aggro.cs:192-198
    public static void HoldAggro(AggroState state, uint target, float now)
    {
        state.Target = target;
        state.HeldSince = now;
        state.DwellUntil = now + AggroDwellMin + (float)state.Rng.NextDouble() * (AggroDwellMax - AggroDwellMin);
    }

    // .Aggro.cs:216-217
    public static float HpShare(SimulatedEntityState state)
        => state.MaxHp > 0 ? (float)state.CurrentHp / state.MaxHp : 1f;

    // .Aggro.cs:219-220
    public static float EnemyReach(NpcBehavior behavior)
        => behavior.AutoAttackStyle == NpcAttackStyle.Ranged ? behavior.AutoAttackRange : MathF.Max(3f, behavior.AutoAttackRange);

    // .Aggro.cs:238-240 (enemy) and :278-280 (ally)
    public static bool EnemyDue(AggroState state, float now, bool currentLive, uint holder)
        => !currentLive || now >= state.DwellUntil ||
           (state.HitByNew && now - state.HeldSince >= AggroHitHold) ||
           holder != state.Holder;

    public static bool AllyDue(AggroState state, float now, object? currentEnemy, uint selectedFocusTargetId)
        => currentEnemy == null || now >= state.DwellUntil ||
           (state.HitByNew && now - state.HeldSince >= AggroHitHold) ||
           selectedFocusTargetId != state.Focus;
}
