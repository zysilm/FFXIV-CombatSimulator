using System.Numerics;
namespace CombatSimulator.Core { public static class Services { public static Logger Log=new(); } public sealed class Logger { public void Info(string value){} } }
namespace CombatSimulator.Rendering.WorldGeometry { public static class WorldGeometryRenderer { public const int MaxVertices=32766; } public static class WorldGeometryBuilder { public static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z); } }
namespace FFXIVClientStructs.FFXIV.Common.Component.BGCollision {
 public struct RaycastHit { public Vector3 Point, Normal, V1,V2,V3; }
 public static class BGCollisionModule {
 public static Action<Vector3>? QueryStart;
 public static bool RaycastMaterialFilter(Vector3 start,Vector3 direction,out RaycastHit hit,float length) {
 QueryStart?.Invoke(start);
 hit=default; if(Math.Abs(direction.Y)<1e-8) return false; float t=-start.Y/direction.Y; if(t<0||t>length) return false;
 hit.Point=start+direction*t;hit.Normal=Vector3.UnitY;hit.V1=new(-100,0,-100);hit.V2=new(0,0,100);hit.V3=new(100,0,-100);return true;
 } }
}
