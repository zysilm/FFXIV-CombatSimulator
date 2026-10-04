using System.Numerics;
namespace CombatSimulator.Core
{
    public static class Services { public static Logger Log = new(); }
    public sealed class Logger { public void Info(string message) { } }
}
namespace CombatSimulator.Rendering.WorldGeometry
{
    public static class WorldGeometryBuilder
    { public static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z); }
}
namespace CombatSimulator.Effects.BodyFluids.Surface
{
    // Only native model/pose and adjacent partial-method boundaries are supplied here.
    // Candidate scan, driver choice, orientation, filtering and binding are production code.
    public sealed unsafe partial class CharacterFluidSurface
    {
        private struct Vertex
        {
            public Vector3 Position;
            public Vector4 Weights, ExtraWeights;
            public int B0, B1, B2, B3, B4, B5, B6, B7;
        }
        private readonly record struct Face(int A, int B, int C, int Slot, int Mesh = 0, uint IndexEntry = 0, int V0 = 0, int V1 = 0, int V2 = 0);
        private readonly record struct ModelLoadKey(int Identity);
        private readonly ModelLoadKey? faceModelKey = new ModelLoadKey(1);
        private readonly ulong objectIdentity = 1;
        private readonly nint drawIdentity = 1;
        private FluidSurfaceAnchor lipAnchor;
        private bool lipBound, poseAvailable = true;
        public string LipStatus { get; private set; } = "Fixture";
        private string[] surfaceBoneNames = Array.Empty<string>();
        private readonly Dictionary<string, int> bones = new();
        private readonly List<Vector3> referencePoints = new();
        private readonly Matrix4x4[] inverseBind = new Matrix4x4[32], boneWorld = new Matrix4x4[32];
        private Vertex[] vertices = Array.Empty<Vertex>();
        private Face[] faces = Array.Empty<Face>();
        private uint frame = 1;
        private Matrix4x4 fixturePose = Matrix4x4.Identity;
        public uint Generation => 1;
        public bool HasSurface => true;
        public bool BudgetExhausted => false;
        public int MouthQueries { get; private set; }
        public bool HasBone(string name) => bones.ContainsKey(name);
        public CharacterFluidSurface(bool custom, Matrix4x4? pose = null)
        {
            int AddBone(string name, Vector3 position)
            {
                int i = bones.Count; bones.Add(name, i); referencePoints.Add(position);
                inverseBind[i] = Matrix4x4.CreateTranslation(-position);
                return i;
            }
            AddBone("j_kosi", new(0, .6f, 0)); AddBone("j_kubi", new(0, 1.4f, 0));
            lowerLipLeft = AddBone("j_f_dlip_02_l", new(-.01f, 1.5f, .1f));
            lowerLipRight = AddBone("j_f_dlip_02_r", new(.01f, 1.5f, .1f));
            upperLipLeft = AddBone("j_f_ulip_02_l", new(-.01f, 1.51f, .1f));
            upperLipRight = AddBone("j_f_ulip_02_r", new(.01f, 1.51f, .1f));
            facialOrigin = AddBone("j_f_face", new(0, 1.5f, 0));
            int left = AddBone(custom ? "iv_c_mune_l" : "j_mune_l", new(-.15f, .9f, 0));
            int right = AddBone(custom ? "iv_c_mune_r" : "j_mune_r", new(.15f, .9f, 0));
            var points = new List<Vertex>(); var triangles = new List<Face>();
            foreach (int driver in new[] { left, right })
            {
                // A forward-facing internal sheet, a real outer sheet >100mm
                // from the driver, and an opposite-facing back sheet.
                foreach ((float depth, bool outward) in new[] { (.035f, true), (.145f, true), (-.03f, false) })
                {
                    var center = referencePoints[driver] + Vector3.UnitZ * depth;
                    int start = points.Count;
                    foreach (var delta in new[] { new Vector3(-.025f, -.02f, 0), new Vector3(.025f, -.02f, 0), new Vector3(0, .025f, 0) })
                        points.Add(new Vertex { Position = center + delta, B0 = driver, Weights = Vector4.UnitX,
                            B1 = 0, B2 = 0, B3 = 0, B4 = 0, B5 = 0, B6 = 0, B7 = 0, ExtraWeights = Vector4.Zero });
                    triangles.Add(outward ? new(start, start + 1, start + 2, 1) : new(start, start + 2, start + 1, 1));
                }
            }
            vertices = points.ToArray(); faces = triangles.ToArray(); SetPose(pose ?? Matrix4x4.Identity);
        }
        public void SetPose(Matrix4x4 pose)
        {
            fixturePose = pose; frame++;
            for (int i = 0; i < bones.Count; i++) boneWorld[i] = Matrix4x4.CreateTranslation(referencePoints[i]) * pose;
        }
        private int ResolveSurfaceBone(string name) => bones.TryGetValue(name, out var i) ? i : -1;
        private static bool Finite(Vector3 p) => CombatSimulator.Rendering.WorldGeometry.WorldGeometryBuilder.Finite(p);
        private static Vector3 SafeNormal(Vector3 p) => Vector3.Normalize(p);
        public bool TryGetMouthAnchor(out FluidSurfaceAnchor anchor)
        { MouthQueries++; anchor = new(Generation, 99, new(.2f, .3f, .5f)); return true; }
        private bool Triangle(int index, out Vector3 a, out Vector3 b, out Vector3 c)
        {
            var face = faces[index];
            a = Vector3.Transform(vertices[face.A].Position, fixturePose);
            b = Vector3.Transform(vertices[face.B].Position, fixturePose);
            c = Vector3.Transform(vertices[face.C].Position, fixturePose); return true;
        }
        public bool TryEvaluate(FluidSurfaceAnchor anchor, out FluidSurfaceSample sample)
        {
            sample = default;
            if (anchor.Generation != Generation || (uint)anchor.Triangle >= faces.Length) return false;
            Triangle(anchor.Triangle, out var a, out var b, out var c);
            sample = new(a * anchor.Barycentric.X + b * anchor.Barycentric.Y + c * anchor.Barycentric.Z,
                Vector3.Normalize(Vector3.Cross(b - a, c - a)), Vector3.Zero); return true;
        }
        public FluidSurfaceAnchor BindLipFixture(bool changedFacialPose)
        {
            var points = new List<Vertex>(); var triangles = new List<Face>();
            void Add(Vector3 a, Vector3 b, Vector3 c)
            {
                int start = points.Count;
                foreach (var point in new[] { a, b, c })
                    points.Add(new Vertex { Position = point, B0 = lowerLipLeft, Weights = Vector4.UnitX,
                        B1 = 0, B2 = 0, B3 = 0, B4 = 0, B5 = 0, B6 = 0, B7 = 0, ExtraWeights = Vector4.Zero });
                triangles.Add(new(start, start + 1, start + 2, 11));
            }
            // A protruding lower bulge and the lower lip's upper supporting edge.
            Add(new(-.012f, 1.49f, .107f), new(.012f, 1.49f, .107f), new(0, 1.502f, .107f));
            Add(new(-.012f, 1.499f, .102f), new(.012f, 1.499f, .102f), new(0, 1.505f, .102f));
            // A closer inward-facing sheet must not win the outward projection.
            Add(new(-.012f, 1.499f, .1005f), new(0, 1.505f, .1005f), new(.012f, 1.499f, .1005f));
            vertices = points.ToArray(); faces = triangles.ToArray();
            surfaceBoneNames = new string[bones.Count];
            foreach (var entry in bones) surfaceBoneNames[entry.Value] = entry.Key;
            if (changedFacialPose)
            {
                boneWorld[lowerLipLeft] *= Matrix4x4.CreateTranslation(.02f, -.025f, .005f);
                boneWorld[lowerLipRight] *= Matrix4x4.CreateTranslation(-.015f, -.02f, .012f);
            }
            BuildLipCandidates(); TrySelectLipCandidate();
            if (!lipBound) throw new Exception(LipStatus);
            return lipAnchor;
        }
    }
}
