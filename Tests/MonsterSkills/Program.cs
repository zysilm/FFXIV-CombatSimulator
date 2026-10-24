using System.Numerics;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

// Offline checks for enemy-skill geometry. Each section names the production rule it pins.
int checks = 0;
void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }

// ---- CastType mapping (measured on the Action sheet; see CastTypeMapping doc) ----
Check(CastTypeMapping.Map(1, 3, true) == AoeShape.Single, "1 single");
Check(CastTypeMapping.Map(2, 25, true) == AoeShape.Circle, "aimed type 2 (Fire II) is target-centred");
Check(CastTypeMapping.Map(2, 0, false) == AoeShape.CircleSelf, "Range-0 type 2 (Overpower) is caster-centred");
Check(CastTypeMapping.Map(2, 3, false) == AoeShape.CircleSelf, "self-targeted monster type 2 (Realm Shaker rng 3) is caster-centred");
Check(CastTypeMapping.Map(3, 0, false) == AoeShape.Cone && CastTypeMapping.Map(13, 3, false) == AoeShape.Cone, "3/13 cones");
Check(CastTypeMapping.Map(4, 0, false) == AoeShape.Line && CastTypeMapping.Map(12, 0, false) == AoeShape.Line, "4/12 rects");
Check(CastTypeMapping.Map(8, 100, true) == AoeShape.Line && CastTypeMapping.IsCharge(8), "8 charge is a rect");
Check(CastTypeMapping.Map(5, 0, false) == AoeShape.CircleSelf, "5 caster circle");
Check(CastTypeMapping.Map(10, 0, false) == AoeShape.Donut, "10 donut");
Check(CastTypeMapping.Map(6, 100, false) == AoeShape.Single && CastTypeMapping.Map(11, 0, false) == AoeShape.Single, "6/11 stay single");
Check(CastTypeMapping.Map(2) == AoeShape.Circle, "context-free 2 keeps the aimed form");
Check(CastTypeMapping.AddsCasterHitbox(3) && CastTypeMapping.AddsCasterHitbox(4) && CastTypeMapping.AddsCasterHitbox(5), "3/4/5 add hitbox");
Check(!CastTypeMapping.AddsCasterHitbox(12) && !CastTypeMapping.AddsCasterHitbox(13) && !CastTypeMapping.AddsCasterHitbox(2), "12/13/2 do not");
Check(CastTypeMapping.ParseConeAngleDeg("gl_fan090_1bf") == 90, "fan090");
Check(CastTypeMapping.ParseConeAngleDeg("er_gl_fan120_1bf") == 120, "er fan120");
Check(CastTypeMapping.ParseConeAngleDeg("gl_fan270_1bf") == 270, "fan270");
Check(CastTypeMapping.ParseConeAngleDeg("general_1bf") == 90 && CastTypeMapping.ParseConeAngleDeg(null) == 90 && CastTypeMapping.ParseConeAngleDeg("xfan") == 90, "fallback 90");

// ---- Geometry primitives ----
var o = Vector3.Zero;
var fwd = new Vector3(0, 0, 10);
Check(CombatGeometry.IsInsideConeAngle(o, fwd, new Vector3(0, 0, 5), 6, 90), "cone centre");
Check(!CombatGeometry.IsInsideConeAngle(o, fwd, new Vector3(4, 0, 2), 6, 90), "outside 90 cone at 63deg");
Check(CombatGeometry.IsInsideConeAngle(o, fwd, new Vector3(4, 0, 2), 6, 150), "inside 150 cone at 63deg");
Check(CombatGeometry.IsInsideConeAngle(o, fwd, new Vector3(2, 0, 2), 6, 90) == CombatGeometry.IsInsideCone(o, fwd, new Vector3(2, 0, 2), 6), "90 deg equals legacy cone");
Check(CombatGeometry.IsInsideDonut(o, new Vector3(5, 0, 0), 3, 8) && !CombatGeometry.IsInsideDonut(o, new Vector3(1, 0, 0), 3, 8), "donut hole is safe");
Check(!CombatGeometry.IsInsideDonut(o, new Vector3(9, 0, 0), 3, 8), "donut outer edge");

// ---- Monster kit selection (rows copied from the m0001 spider family) ----
MonsterActionCandidate Row(uint id, string name, byte ct, float rng, float r, float x, float cast, float recast,
    uint cat = 3, uint atk = 3, bool hostile = false, bool omen = false, ushort tl = 0, bool allies = false)
    => new(id, name, ct, rng, r, x, cast, recast, cat, atk, hostile, allies, false, omen, tl == 0 ? (ushort)id : tl);
var spider = new[]
{
    Row(327, "Sticky Web", 1, 3, 0, 0, 0, 6, atk: 0, hostile: true),          // status only
    Row(329, "Deadly Thrust", 1, 3, 0, 0, 0, 6, atk: 2, hostile: true, tl: 50),
    Row(330, "Deadly Thrust", 1, 3, 0, 0, 0, 6, atk: 0, hostile: true, tl: 50), // same timeline
    Row(697, "Realm Shaker", 5, 0, 6, 0, 0, 20),
    Row(701, "Silkscreen", 4, 0, 15, 4, 2.5f, 30, atk: 5, omen: true),
    Row(702, "Deadly Thrust", 2, 0, 60, 0, 2, 20),                            // room-wide
    Row(704, "Tail Molt", 1, 0, 0, 0, 0, 0, cat: 0, atk: 0),
    Row(900, "Heal Self", 2, 0, 5, 0, 0, 10, allies: true),
    Row(901, "Charge", 8, 100, 0, 8, 4, 0, hostile: true, omen: true),
    Row(902, "Queasy Cloud", 5, 0, 6, 0, 0, 6, atk: 0),
};
var kit = MonsterKitBuilder.Select(spider, 3);
Check(kit.Count == 3, $"spider kit size {kit.Count}");
Check(kit[0].ActionId == 701, "telegraphed Silkscreen leads");
Check(kit.Any(k => k.ActionId == 697), "Realm Shaker kept");
Check(kit.Count(k => k.Name == "Deadly Thrust") == 1 && kit.Any(k => k.ActionId == 329), "one damaging Deadly Thrust (single)");
Check(!kit.Any(k => k.ActionId is 327 or 702 or 704 or 900 or 901 or 902), "status/room-wide/utility/ally/charge rows dropped");
Check(MonsterKitBuilder.Select(spider, 1).Single().ActionId == 701, "max 1 keeps the best area skill");
Check(MonsterKitBuilder.Select(spider, 0).Count == 0, "max 0 empty");
Check(MonsterKitBuilder.Select(Array.Empty<MonsterActionCandidate>(), 3).Count == 0, "empty family");
var cone = Row(317, "Foul Breath", 3, 0, 6, 0, 2.5f, 6, atk: 6, omen: true);
Check(MonsterKitBuilder.EngageRange(cone) is >= 2.5f and <= 5f, "cone engage stays in melee");
Check(MonsterKitBuilder.EngageRange(spider[1]) == 3f, "single engage melee");
Check(MonsterKitBuilder.Cooldown(cone) == 12.5f, "cooldown floor 10 + cast");
Check(MonsterKitBuilder.Potency(5000, 110, true, 2.5f) == 330 && MonsterKitBuilder.Potency(10, 110, true, 0) == 154, "potency band");
Check(MonsterKitBuilder.Potency(5000, 110, false, 0) == 220, "instant ceiling 2x");

// ---- Telegraph snapshot: what is drawn is what hits ----
var caster = new Vector3(10, 0, 10);
var tgt = new Vector3(10, 0, 14);       // 4y north (+Z) of the caster
const float hb = 1.0f;
// Foul Breath: CastType 3, r6, 90° → cone from the caster, reach 6 + hitbox.
var breath = TelegraphShape.Build(AoeShape.Cone, 3, 6, 0, 90, 0, caster, hb, tgt, 2.5f);
Check(breath.Kind == TelegraphKind.Cone && breath.Origin == caster && breath.IsArea, "cone anchored on caster");
Check(MathF.Abs(breath.Rotation) < 1e-4f, "cone faces +Z target (yaw 0)");
Check(breath.Contains(tgt) && breath.Contains(new Vector3(10, 0, 16.9f)), "target and 6.9y ahead inside (hitbox added)");
Check(!breath.Contains(new Vector3(10, 0, 17.2f)), "beyond reach");
Check(!breath.Contains(new Vector3(10, 0, 8)), "behind the caster is safe");
Check(!breath.Contains(new Vector3(14, 0, 11)), "side step out of a 90° cone");
Check(breath.Contains(caster), "standing on the caster's centre");
var wide = TelegraphShape.Build(AoeShape.Cone, 13, 6, 0, 270, 0, caster, hb, tgt, 2.5f);
Check(wide.Contains(new Vector3(14, 0, 9.5f)) && wide.Radius == 6, "270° cone reaches behind; type 13 adds no hitbox");
// Yaw convention: target due east (+X) → yaw +90°.
var east = TelegraphShape.Build(AoeShape.Cone, 13, 6, 0, 60, 0, caster, 0, new Vector3(15, 0, 10), 2.5f);
Check(MathF.Abs(east.Rotation - MathF.PI / 2) < 1e-4f && east.Contains(new Vector3(14, 0, 10)) && !east.Contains(new Vector3(10, 0, 14)), "yaw = atan2(dx, dz)");
// Silkscreen: CastType 4 rect 15 long, 4 wide.
var silk = TelegraphShape.Build(AoeShape.Line, 4, 15, 4, 90, 0, caster, hb, tgt, 2.5f);
Check(silk.Kind == TelegraphKind.Rect && silk.Contains(new Vector3(11.9f, 0, 25.5f)) && !silk.Contains(new Vector3(12.1f, 0, 20)), "rect width/length");
Check(!silk.Contains(new Vector3(10, 0, 9)), "rect does not extend behind");
// Charge (8): EffectRange 0 → reaches the target.
var charge = TelegraphShape.Build(AoeShape.Line, 8, 0, 8, 90, 0, caster, hb, tgt, 2.5f);
Check(charge.IsArea && charge.Contains(tgt) && charge.Radius > 4, "charge reaches target");
// Self circle (5) vs target circle (2 aimed).
var pbaoe = TelegraphShape.Build(AoeShape.CircleSelf, 5, 6, 0, 90, 0, caster, hb, tgt, 2.5f);
Check(pbaoe.Origin == caster && pbaoe.Radius == 7, "type 5 on caster + hitbox");
var fire2 = TelegraphShape.Build(AoeShape.Circle, 2, 5, 0, 90, 0, caster, hb, tgt, 2.5f);
Check(fire2.Origin == tgt && fire2.Radius == 5, "aimed circle on target, no hitbox");
var donut = TelegraphShape.Build(AoeShape.Donut, 10, 10, 0, 90, 4, caster, hb, tgt, 2.5f);
Check(!donut.Contains(new Vector3(12, 0, 10)) && donut.Contains(new Vector3(16, 0, 10)), "donut hole");
// Single target / sizeless → legacy 2.5y circle on the target, not an area.
var single = TelegraphShape.Build(AoeShape.Single, 1, 0, 0, 90, 0, caster, hb, tgt, 2.5f);
Check(!single.IsArea && single.Origin == tgt && single.Radius == 2.5f, "single fallback circle");
Check(!TelegraphShape.Build(AoeShape.Cone, 3, 0, 0, 90, 0, caster, hb, tgt, 2.5f).IsArea, "sizeless cone falls back");
// Snapshot is fixed: the shape does not follow a target that moves after commit.
var moved = new Vector3(14, 0, 10);
Check(!breath.Contains(moved), "moving out after snapshot dodges");

Console.WriteLine($"MonsterSkills: {checks} checks passed.");
