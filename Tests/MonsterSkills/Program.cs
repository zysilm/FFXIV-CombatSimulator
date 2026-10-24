using System.Numerics;
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

Console.WriteLine($"MonsterSkills: {checks} checks passed.");
