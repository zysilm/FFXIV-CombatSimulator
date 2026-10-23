using System.Numerics;
using CombatSimulator.Animation;

// Production look-at math: model space forward +Z, up +Y.
int checks = 0;
void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
const float Deg = MathF.PI / 180f;
float MaxYaw = 70 * Deg, MaxPitch = 35 * Deg;
var head = new Vector3(0, 1.6f, 0);
Vector3 Dir(float yawDeg, float pitchDeg) => new(MathF.Sin(yawDeg * Deg) * MathF.Cos(pitchDeg * Deg), MathF.Sin(pitchDeg * Deg), MathF.Cos(yawDeg * Deg) * MathF.Cos(pitchDeg * Deg));
float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1, 1)) / Deg;
// Facing after applying the swing to the bone, measured the same way the controller does.
Vector3 Facing(Quaternion swing, Quaternion current, Quaternion bind) =>
    Vector3.Transform(Vector3.UnitZ, Quaternion.Normalize(swing * current * Quaternion.Inverse(bind)));

// Several bind conventions: real bones' local axes are not aligned with the model.
var binds = new[] { Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
    Quaternion.CreateFromYawPitchRoll(1.1f, -.4f, 2.3f) };
foreach (var bind in binds)
{
    // Ahead: nothing to do.
    var swing = HeadLookAtMath.Swing(bind, bind, head, head + Dir(0, 0) * 2, MaxYaw, MaxPitch);
    Check(MathF.Abs(swing.W) > .99999f, "target straight ahead still turns the head");
    // Within range: looks exactly at it.
    foreach (var (yaw, pitch) in new[] { (30f, 0f), (-55f, 10f), (20f, -25f) })
    {
        swing = HeadLookAtMath.Swing(bind, bind, head, head + Dir(yaw, pitch) * 3, MaxYaw, MaxPitch);
        Check(Angle(Facing(swing, bind, bind), Dir(yaw, pitch)) < .05f, $"does not look at ({yaw},{pitch}) with bind {bind}");
    }
    // Out of range: stops at the limit instead of twisting the neck around.
    swing = HeadLookAtMath.Swing(bind, bind, head, head + Dir(180, 0) * 3, MaxYaw, MaxPitch);
    var behind = Facing(swing, bind, bind);
    Check(MathF.Abs(MathF.Abs(MathF.Atan2(behind.X, behind.Z) / Deg) - 70) < .1f, "target behind is not clamped to 70 degrees");
    swing = HeadLookAtMath.Swing(bind, bind, head, head + Dir(0, 80) * 3, MaxYaw, MaxPitch);
    Check(MathF.Abs(MathF.Asin(Facing(swing, bind, bind).Y) / Deg - 35) < .1f, "steep target is not clamped to 35 degrees");
    // Animation already turned the head 40 degrees right: look-at corrects relative to that.
    var animated = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 40 * Deg) * bind;
    swing = HeadLookAtMath.Swing(animated, bind, head, head + Dir(0, 0) * 3, MaxYaw, MaxPitch);
    Check(Angle(Facing(swing, animated, bind), Vector3.UnitZ) < .05f, "animated head turn not taken into account");
    // Limits are measured from the animated direction.
    swing = HeadLookAtMath.Swing(animated, bind, head, head + Dir(-90, 0) * 3, MaxYaw, MaxPitch);
    var limited = Facing(swing, animated, bind);
    Check(MathF.Abs(MathF.Atan2(limited.X, limited.Z) / Deg - (40 - 70)) < .1f, "limit not relative to animated facing");
    // The bone-local delta given to ApplyRotationDeltas reproduces the model-space swing.
    swing = HeadLookAtMath.Swing(animated, bind, head, head + Dir(25, 15) * 3, MaxYaw, MaxPitch);
    var viaDelta = Quaternion.Normalize(animated * HeadLookAtMath.LocalDelta(animated, swing));
    Check(MathF.Abs(Quaternion.Dot(viaDelta, Quaternion.Normalize(swing * animated))) > .99999f, "local delta does not reproduce the swing");
    // No twist: the head's up stays as close as possible to its animated up.
    var up = Vector3.Transform(Vector3.UnitY, Quaternion.Normalize(swing * animated * Quaternion.Inverse(bind)));
    Check(Vector3.Dot(up, Vector3.UnitY) > .9f, "look-at rolls the head");
}
// Degenerate inputs leave the animation alone.
Check(MathF.Abs(HeadLookAtMath.Swing(Quaternion.Identity, Quaternion.Identity, head, head, MaxYaw, MaxPitch).W) > .99999f, "target at the bone");
Check(MathF.Abs(HeadLookAtMath.Swing(Quaternion.Identity, Quaternion.Identity, head, new Vector3(float.NaN), MaxYaw, MaxPitch).W) > .99999f, "NaN target");
// A living observer keeps watching the defeated player even while a victory, grab,
// Enemy Control or Fighting controller owns that observer. The observer's own death
// always releases its pose to ragdoll instead of looking from a corpse.
var enemy = new LookAtActor(100, 1);
var player = new LookAtActor(200, 2);
var companion = new LookAtActor(300, 3);
Check(HeadLookAtTargetPolicy.Select(enemy, companion, player, false)==companion,"Living player's combat target selection changed");
foreach(bool controlled in new[]{false,true})
{
    Check(HeadLookAtTargetPolicy.CanDrive(true,controlled,true),"Defeat look-at excluded a controlled living enemy");
    Check(HeadLookAtTargetPolicy.Select(enemy,companion,player,true)==player,"Defeated player lost priority to companion");
    Check(!HeadLookAtTargetPolicy.CanDrive(false,controlled,true),"Dead observer fights ragdoll ownership");
    Check(HeadLookAtTargetPolicy.Select(enemy,default,player,true)==player,"No living combat target dropped corpse look-at");
}
Check(!HeadLookAtTargetPolicy.CanDrive(true,true,false),"Alive player's other controller lost pose ownership");
Check(HeadLookAtTargetPolicy.Select(enemy,default,player,false)==null,"Missing combat target fabricated a target");
Check(HeadLookAtTargetPolicy.Select(enemy,companion,default,true)==null,"Despawned defeated player used invalid native memory");
Check(HeadLookAtTargetPolicy.Select(enemy,enemy,player,false)==null,"Observer targeted itself");
Check(HeadLookAtTargetPolicy.Select(enemy,new LookAtActor(enemy.Address,99),player,false)==null,"Reused native slot became a self-target");
Check(HeadLookAtTargetPolicy.Select(enemy,new LookAtActor(300,0),player,false)==null,"Unknown entity identity accepted");
Console.WriteLine($"PASS {checks} checks; look-at math and target policy across defeat, grabs/stages, observer death, missing identities and revival.");
