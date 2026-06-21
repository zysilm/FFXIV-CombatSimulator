using System;

namespace CombatSimulator.Simulation;

/// <summary>
/// Action-game tuning layer for skills whose real combat potency is not available from client data.
/// Lumina supplies structure (range, area, cast/recast, native MP signal); this model turns that into
/// a consistent virtual MP price and per-target potency anchored to the configured basic attack.
/// </summary>
public static class VirtualActionModel
{
    private const int MinActionCost = 1500;
    private const int MaxActionCost = 8500;
    private const float MaxPowerCost = 8000f;

    public static void Apply(ActionData data, int basicPotency)
    {
        var basePotency = Math.Max(1, basicPotency);

        if (IsBasicAttack(data))
        {
            data.MpCost = 0;
            data.Potency = basePotency;
            data.ComboPotency = 0;
            return;
        }

        data.MpCost = CalculateMpCost(data);
        data.Potency = CalculatePotency(data, basePotency);
        if (data.IsComboAction)
            data.ComboPotency = Math.Max(data.Potency, (int)MathF.Round(data.Potency * 1.15f));
    }

    public static int CalculateMpCost(ActionData data)
    {
        if (IsBasicAttack(data))
            return 0;

        var nativeMpSignal = MathF.Min(MathF.Max(0, data.NativeMpCost), 2000) * 0.45f;
        var recast = MathF.Max(0f, data.RecastTime);
        var commit = CommitTime(data);
        var baseCost = CommitBaseCost(commit);

        // Recast is only a secondary signal now. The primary action-game tier is the
        // actual release/animation commitment, which Action Mode resolves from TMB.
        var cooldownCost = MathF.Min(MathF.Max(0f, recast - 2.5f) * 25f, 1200f);
        var expectedTargets = ExpectedTargets(data);
        var aoeCost = MathF.Max(0f, expectedTargets - 1f) * 180f;
        var rangeCost = data.Range <= 5f ? 0f : data.Range <= 15f ? 220f : 380f;

        var cost = nativeMpSignal + baseCost + cooldownCost + aoeCost + rangeCost;
        return (int)MathF.Round(Math.Clamp(cost, MinActionCost, MaxActionCost));
    }

    public static int CalculatePotency(ActionData data, int basicPotency)
    {
        if (IsBasicAttack(data))
            return Math.Max(1, basicPotency);

        var basePotency = Math.Max(1, basicPotency);
        var multiplier = CalculateMultiplier(data);
        var potency = basePotency * multiplier;
        return (int)MathF.Round(Math.Clamp(potency, basePotency * 0.65f, basePotency * 12.0f));
    }

    public static float CalculateMultiplier(ActionData data)
    {
        if (IsBasicAttack(data))
            return 1f;

        var cost = Math.Clamp(data.MpCost > 0 ? data.MpCost : CalculateMpCost(data), MinActionCost, MaxActionCost);
        var singleBudget = 1f + 9f * MathF.Pow(MathF.Min(cost, MaxPowerCost) / MaxPowerCost, 1.25f);
        var rangeFactor = data.Range <= 5f ? 1.10f : data.Range <= 15f ? 1.0f : 0.90f;
        var castFactor = 1f + MathF.Min(MathF.Max(0f, data.CastTime), 3f) * 0.06f;
        var commitFactor = 1f + MathF.Min(MathF.Max(0f, CommitTime(data) - 0.8f), 1.7f) * 0.04f;

        if (!IsAoe(data))
            return singleBudget * rangeFactor * castFactor * commitFactor;

        var aoeBudget = AoeBudget(data, cost);
        return singleBudget * aoeBudget * AoeShapeFactor(data) * rangeFactor * castFactor * commitFactor;
    }

    public static float AoeActualTargetFalloff(ActionData data, int actualTargets)
    {
        if (!IsAoe(data) || actualTargets <= 1)
            return 1f;

        var expectedTargets = MathF.Max(1f, ExpectedTargets(data));
        return Math.Clamp(MathF.Sqrt(expectedTargets / actualTargets), 0.55f, 1f);
    }

    public static float ExpectedTargets(ActionData data)
    {
        var radius = MathF.Max(0f, data.Radius);
        return data.Shape switch
        {
            AoeShape.Single => 1.0f,
            AoeShape.Line => data.Width <= 3f ? 1.5f : 1.8f,
            AoeShape.Cone => radius <= 6f ? 1.6f : radius <= 10f ? 2.0f : 2.3f,
            AoeShape.Circle or AoeShape.CircleSelf or AoeShape.GroundCircle =>
                radius <= 5f ? 2.0f : radius <= 8f ? 2.8f : 3.5f,
            AoeShape.Donut => 3.0f,
            _ => 1.0f,
        };
    }

    public static bool IsAoe(ActionData data)
        => data.Shape != AoeShape.Single && data.Radius > 0f;

    private static float AoeBudget(ActionData data, int cost)
    {
        var t = Math.Clamp((cost - MinActionCost) / (MaxPowerCost - MinActionCost), 0f, 1f);
        var budget = 0.36f + 0.19f * t;

        // Keep the largest AoEs near half of an equivalent single-target budget.
        if (ExpectedTargets(data) >= 3f)
            budget -= 0.06f;

        return Math.Clamp(budget, 0.34f, 0.55f);
    }

    private static float AoeShapeFactor(ActionData data)
    {
        var radius = MathF.Max(0f, data.Radius);
        return data.Shape switch
        {
            AoeShape.Line => data.Width <= 3f ? 1.12f : 1.0f,
            AoeShape.Cone => radius <= 6f ? 1.08f : radius <= 10f ? 1.0f : 0.92f,
            AoeShape.CircleSelf => radius <= 5f ? 0.95f : radius <= 8f ? 0.86f : 0.78f,
            AoeShape.Circle or AoeShape.GroundCircle => radius <= 5f ? 0.90f : radius <= 8f ? 0.82f : 0.74f,
            AoeShape.Donut => 0.78f,
            _ => 1f,
        };
    }

    private static float CommitTime(ActionData data)
    {
        var cast = MathF.Max(0f, data.CastTime);
        if (data.AnimationDuration > 0.05f)
            return MathF.Max(cast, data.AnimationDuration * 0.70f);

        var recast = MathF.Max(0f, data.RecastTime);
        var fallbackAnimation = recast switch
        {
            <= 0f => 0.75f,
            <= 3.0f => 0.85f,
            <= 30.0f => 1.20f,
            <= 60.0f => 1.60f,
            _ => 2.10f,
        };
        return MathF.Max(cast, fallbackAnimation * 0.70f);
    }

    private static float CommitBaseCost(float commit)
    {
        commit = MathF.Max(0f, commit);
        if (commit <= 0.4f)
            return MinActionCost;
        if (commit <= 0.8f)
            return Lerp(1500f, 2500f, (commit - 0.4f) / 0.4f);
        if (commit <= 1.3f)
            return Lerp(2500f, 4000f, (commit - 0.8f) / 0.5f);
        if (commit <= 2.0f)
            return Lerp(4000f, 6500f, (commit - 1.3f) / 0.7f);
        return Lerp(6500f, MaxActionCost, Math.Clamp((commit - 2.0f) / 1.0f, 0f, 1f));
    }

    private static float Lerp(float a, float b, float t)
        => a + (b - a) * Math.Clamp(t, 0f, 1f);

    private static bool IsBasicAttack(ActionData data)
        => data.ActionId is 0 or 7;
}
