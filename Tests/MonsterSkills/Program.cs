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

Console.WriteLine($"MonsterSkills: {checks} checks passed.");
