using System.Numerics;
using CombatSimulator.Camera.DeathShots;
using CombatSimulator.Camera.Duel;

namespace DuelCamera;

/// <summary>What one run of one camera over one scripted duel measured.</summary>
public sealed class Result
{
    public string Name = "";
    public int Frames, TwoFrames;
    /// <summary>Frames (with an opponent) on which both fighters are in the picture, head and mid-body; both heads;
    /// the player, head and mid-body (all frames).</summary>
    public int BothIn, HeadsIn, PlayerIn;
    /// <summary>The same, after the first <see cref="Metrics.Entry"/> seconds.</summary>
    public int SettledTwoFrames, SettledBothIn, SettledHeadsIn;
    /// <summary>The fighters are on opposite halves of the picture.</summary>
    public int OppositeSides;
    public readonly List<float> Separation = new(), PlayerSize = new(), OpponentSize = new();
    public readonly List<float> EyeSpeed = new(), EyeSpeedRelative = new(), ViewRate = new(), ViewAccel = new(), EyeJerk = new(), FovRate = new();
    /// <summary>How far the yaw the player moves by changed since the frame before (the game may read it one frame
    /// late), and how far it is from the rendered yaw; degrees.</summary>
    public readonly List<float> MoveYawError = new(), ViewVsMove = new();
    /// <summary>Angle between the way "forward" moves the player and the direction to the opponent, degrees.</summary>
    public readonly List<float> ForwardOffOpponent = new();
    public int LineCrossings, SideChanges, Reanchors, Cuts;
    public float MinGround = float.PositiveInfinity, MinToOpponent = float.PositiveInfinity, MinToPlayer = float.PositiveInfinity;
    public int BehindWall, PlayerHidden, OpponentHidden, PulledFrames;
    public float MaxRecovery = -1f;
    /// <summary>Longest time from a change of opponent to both heads in the picture (and staying).</summary>
    public float MaxSwitch = -1f;
    public float EntryBothIn = -1f;
    public float MaxRoll;
    public bool Finite = true;
    public readonly List<string> Events = new();
}

public static class Metrics
{
    /// <summary>The first seconds of a run are the way in from the game's camera.</summary>
    public const float Entry = 1.5f;
    /// <summary>After a change of opponent the camera is on its way to the new two-shot for this long.</summary>
    public const float AfterSwitch = 1.2f;

    public static float Percentile(List<float> values, float p)
    {
        if (values.Count == 0) return 0f;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[Math.Clamp((int)MathF.Round(p * (sorted.Length - 1)), 0, sorted.Length - 1)];
    }

    public static bool Inside(Vector3 ndc) => ndc.Z > 0.05f && MathF.Abs(ndc.X) <= 1f && MathF.Abs(ndc.Y) <= 1f;

    public static Result Measure(Scenario scenario, List<Frame> frames, TestWorld world)
    {
        var r = new Result { Name = scenario.Name, Frames = frames.Count };
        Vector3 lastForward = default, lastEye = default, lastRel = default, lastRelVel = default, lastRelAcc = default, lastNormal = default;
        var lastRate = 0f;
        var lastFov = 0f;
        var history = 0;
        var lineSide = 0;
        var haveNormal = false;
        var lastOpponent = 0u;
        var inSince = -1f;
        float switchedAt = -100f, switchIn = -1f;
        var switchOpen = false;

        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            var o = f.Out;
            var forward = o.LookAt - o.Eye;
            if (!float.IsFinite(forward.LengthSquared()) || forward.LengthSquared() < 1e-8f || !float.IsFinite(o.Eye.LengthSquared()) || !float.IsFinite(o.Fov))
            {
                r.Finite = false;
                continue;
            }
            forward = Vector3.Normalize(forward);
            Vector3 P(Vector3 point) => DuelView.Project(o.Eye, forward, o.Fov, Runner.Aspect, point);

            // ---- framing ----
            var pHead = P(f.Player.Head);
            var pLow = P(f.Player.At(0.5f));
            var playerIn = Inside(pHead) && Inside(pLow);
            if (playerIn) r.PlayerIn++;
            var pFeet = P(f.Player.Feet);
            if (pHead.Z > 0.05f && pFeet.Z > 0.05f) r.PlayerSize.Add(MathF.Abs(pHead.Y - pFeet.Y) * 0.5f);
            if (f.Opponent is { } opp)
            {
                r.TwoFrames++;
                var oHead = P(opp.Head);
                var oLow = P(opp.At(0.5f));
                var both = playerIn && Inside(oHead) && Inside(oLow);
                var heads = Inside(pHead) && Inside(oHead);
                if (both) r.BothIn++;
                if (heads) r.HeadsIn++;
                if (f.Picked != null && i > 0)
                {
                    switchedAt = f.T;
                    switchIn = -1f;
                    switchOpen = true;
                }
                if (switchOpen)
                {
                    if (heads)
                    {
                        if (switchIn < 0f) switchIn = f.T;
                        if (f.T - switchIn >= 0.3f)
                        {
                            r.MaxSwitch = MathF.Max(r.MaxSwitch, switchIn - switchedAt);
                            switchOpen = false;
                        }
                    }
                    else switchIn = -1f;
                }
                if (f.T >= Entry && f.T >= switchedAt + AfterSwitch)
                {
                    r.SettledTwoFrames++;
                    if (both) r.SettledBothIn++;
                    if (heads) r.SettledHeadsIn++;
                }
                if (r.EntryBothIn < 0f)
                {
                    if (both)
                    {
                        if (inSince < 0f) inSince = f.T;
                        if (f.T - inSince >= 0.5f) r.EntryBothIn = inSince;
                    }
                    else inSince = -1f;
                }
                var pc = P(f.Player.At(0.7f));
                var oc = P(opp.At(0.7f));
                if (pc.Z > 0.05f && oc.Z > 0.05f)
                {
                    r.Separation.Add(MathF.Abs(pc.X - oc.X));
                    if (pc.X * oc.X < 0f) r.OppositeSides++;
                }
                var oFeet = P(opp.Feet);
                if (oHead.Z > 0.05f && oFeet.Z > 0.05f) r.OpponentSize.Add(MathF.Abs(oHead.Y - oFeet.Y) * 0.5f);

                // The line between the fighters, as an undirected line: its normal keeps its sense from frame to
                // frame, so the fighters swapping ends does not count; only the eye going over the line does.
                var axis = opp.At(0.7f) - f.Player.At(0.7f);
                axis.Y = 0f;
                if (f.OpponentId != lastOpponent || o.Cut)
                {
                    haveNormal = false;
                    lineSide = 0;
                }
                if (axis.Length() >= 1f)
                {
                    var normal = Vector3.Normalize(new Vector3(axis.Z, 0f, -axis.X));
                    if (haveNormal && Vector3.Dot(normal, lastNormal) < 0f) normal = -normal;
                    lastNormal = normal;
                    haveNormal = true;
                    var lateral = Vector3.Dot(o.Eye - f.Player.At(0.7f), normal);
                    var s = lateral > 0.5f ? 1 : lateral < -0.5f ? -1 : 0;
                    if (s != 0)
                    {
                        if (lineSide != 0 && s != lineSide)
                        {
                            r.LineCrossings++;
                            r.Events.Add($"{f.T:0.00} s: the eye crossed the line between the fighters");
                        }
                        lineSide = s;
                    }
                }
                lastOpponent = f.OpponentId;

                // "Forward" on screen against the way to the opponent.
                var toOpp = opp.Feet - f.Player.Feet;
                toOpp.Y = 0f;
                if (toOpp.Length() > 1.5f)
                {
                    var move = Scenarios.MoveAxes(o.MoveYaw).Forward;
                    r.ForwardOffOpponent.Add(MathF.Acos(Math.Clamp(Vector3.Dot(move, Vector3.Normalize(toOpp)), -1f, 1f)) / DeathShotMath.Deg);
                }

                // Not inside the opponent.
                var top = opp.Head;
                var seg = top - opp.Feet;
                var k = seg.LengthSquared() > 1e-6f ? Math.Clamp(Vector3.Dot(o.Eye - opp.Feet, seg) / seg.LengthSquared(), 0f, 1f) : 0f;
                r.MinToOpponent = MathF.Min(r.MinToOpponent, Vector3.Distance(o.Eye, opp.Feet + seg * k) - opp.Radius);
                if (world.Blocked(opp.Head, o.Eye, out _)) r.OpponentHidden++;
            }
            else
            {
                haveNormal = false;
                lineSide = 0;
                lastOpponent = 0;
            }

            // ---- the world ----
            var ground = world.Ground(o.Eye.X, o.Eye.Z);
            r.MinGround = MathF.Min(r.MinGround, o.Eye.Y - ground);
            {
                var seg = f.Player.Head - f.Player.Feet;
                var k = seg.LengthSquared() > 1e-6f ? Math.Clamp(Vector3.Dot(o.Eye - f.Player.Feet, seg) / seg.LengthSquared(), 0f, 1f) : 0f;
                r.MinToPlayer = MathF.Min(r.MinToPlayer, Vector3.Distance(o.Eye, f.Player.Feet + seg * k));
            }
            if (world.WallBetween(f.Player.At(0.9f), o.Eye)) r.BehindWall++;
            if (world.Blocked(f.Player.Head, o.Eye, out _)) r.PlayerHidden++;
            if (o.Pull < 0.98f) r.PulledFrames++;

            // ---- motion ----
            var rel = o.Eye - f.Player.Feet;
            r.MoveYawError.Add(MathF.Abs(DeathShotMath.AngleDelta(f.MoveYaw, o.MoveYaw)) / DeathShotMath.Deg);
            r.ViewVsMove.Add(MathF.Abs(DeathShotMath.AngleDelta(o.Yaw, o.MoveYaw)) / DeathShotMath.Deg);
            if (o.Cut)
            {
                if (i > 0) r.Cuts++;
                history = 0;
            }
            else if (history > 0)
            {
                r.EyeSpeed.Add(Vector3.Distance(o.Eye, lastEye) / f.Dt);
                var relVel = (rel - lastRel) / f.Dt;
                r.EyeSpeedRelative.Add(relVel.Length());
                var rate = MathF.Acos(Math.Clamp(Vector3.Dot(forward, lastForward), -1f, 1f)) / DeathShotMath.Deg / f.Dt;
                r.ViewRate.Add(rate);
                r.FovRate.Add(MathF.Abs(o.Fov - lastFov) / f.Dt);
                if (history > 1)
                {
                    r.ViewAccel.Add(MathF.Abs(rate - lastRate) / f.Dt);
                    var relAcc = (relVel - lastRelVel) / f.Dt;
                    if (history > 2) r.EyeJerk.Add(((relAcc - lastRelAcc) / f.Dt).Length());
                    lastRelAcc = relAcc;
                }
                lastRate = rate;
                lastRelVel = relVel;
            }
            history++;
            lastForward = forward;
            lastEye = o.Eye;
            lastRel = rel;
            lastFov = o.Fov;

            // No roll: the view is built from an eye and a look-at point with the world's up.
            if (o.SideChanged) r.Events.Add($"{f.T:0.00} s: side → {o.Side} ({o.Reason})");
            if (o.Reanchored) r.Events.Add($"{f.T:0.00} s: side picked again → {o.Side} ({o.Reason})");
            if (f.Picked != null) r.Events.Add($"{f.T:0.00} s: opponent → {f.OpponentId} ({f.Picked})");
            r.SideChanges = o.SideChanges;
            r.Reanchors = o.Reanchors;
        }

        // ---- time back to the two-shot after a dash through the other fighter ----
        foreach (var end in scenario.DashEnds)
        {
            var recovered = -1f;
            var since = -1f;
            foreach (var f in frames)
            {
                if (f.T < end || f.Opponent is not { } opp) continue;
                var o = f.Out;
                var forward = Vector3.Normalize(o.LookAt - o.Eye);
                var ok = Inside(DuelView.Project(o.Eye, forward, o.Fov, Runner.Aspect, f.Player.Head)) &&
                         Inside(DuelView.Project(o.Eye, forward, o.Fov, Runner.Aspect, opp.Head)) &&
                         MathF.Abs(MathF.Abs(o.OffAxis) - DuelCameraSolver.SideAngle(o.Separation)) < 12f * DeathShotMath.Deg;
                if (ok)
                {
                    if (since < 0f) since = f.T;
                    if (f.T - since >= 0.5f)
                    {
                        recovered = since - end;
                        break;
                    }
                }
                else since = -1f;
            }
            r.MaxRecovery = MathF.Max(r.MaxRecovery, recovered < 0f ? 99f : recovered);
        }
        return r;
    }
}
