using System;

namespace CombatSimulator.Simulation;

/// <summary>
/// Action-game tuning layer for skills whose real combat potency is not available from client data.
/// Lumina supplies structure (range, area, cast/recast, native MP signal); this model turns that into
/// a consistent virtual MP price and per-target potency anchored to the configured basic attack.
/// </summary>
public static class VirtualActionModel
{
    private const int MinActionCost = 100;
    private const int MaxActionCost = 4200;
    private const float MaxPowerCost = 4000f;

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

        var nativeMpSignal = MathF.Min(MathF.Max(0, data.NativeMpCost), 2000) * 0.35f;
        var recast = MathF.Max(0f, data.RecastTime);
        var cast = MathF.Max(0f, data.CastTime);

        var baseCost = recast switch
        {
            >= 2.0f and <= 3.0f => cast > 0f ? 450f : 350f,
            > 3.0f => 700f,
            _ => 250f,
        };

        var cooldownCost = MathF.Min(MathF.Max(0f, recast - 2.5f) * 32f, 2800f);
        var expectedTargets = ExpectedTargets(data);
        var aoeCost = MathF.Max(0f, expectedTargets - 1f) * 260f;
        var rangeCost = data.Range <= 5f ? 0f : data.Range <= 15f ? 100f : 180f;
        var castDiscount = MathF.Min(cast, 3f) * 90f;

        var cost = nativeMpSignal + baseCost + cooldownCost + aoeCost + rangeCost - castDiscount;
        return (int)MathF.Round(Math.Clamp(cost, MinActionCost, MaxActionCost));
    }

    public static int CalculatePotency(ActionData data, int basicPotency)
    {
        if (IsBasicAttack(data))
            return Math.Max(1, basicPotency);

        var basePotency = Math.Max(1, basicPotency);
        var cost = Math.Clamp(data.MpCost > 0 ? data.MpCost : CalculateMpCost(data), MinActionCost, MaxActionCost);
        var expectedTargets = ExpectedTargets(data);

        var costFactor = 1f + 4.8f * MathF.Pow(MathF.Min(cost, MaxPowerCost) / MaxPowerCost, 0.72f);
        var rangeFactor = data.Range <= 5f ? 1.15f : data.Range <= 15f ? 1.0f : 0.92f;
        var targetFactor = 1f / MathF.Sqrt(MathF.Max(1f, expectedTargets));
        var castFactor = 1f + MathF.Min(MathF.Max(0f, data.CastTime), 3f) * 0.08f;
        var cooldownFactor = 1f + MathF.Min(MathF.Max(0f, data.RecastTime - 2.5f), 90f) * 0.003f;

        var potency = basePotency * costFactor * rangeFactor * targetFactor * castFactor * cooldownFactor;
        return (int)MathF.Round(Math.Clamp(potency, basePotency * 1.15f, basePotency * 8.0f));
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

    private static bool IsBasicAttack(ActionData data)
        => data.ActionId is 0 or 7;
}
