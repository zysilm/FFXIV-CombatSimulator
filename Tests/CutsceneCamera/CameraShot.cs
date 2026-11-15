using System.Numerics;

namespace CutsceneCamera;

/// <summary>An attach slot on a camera shot: cutscene actor index (255 = none) and a BIND POINT id from the actor's
/// skeleton .eid table (e.g. 76 = n_root + face height, 71 = n_root, 26/1/27 = j_kao, 28 = j_sebo_c, 18 = n_hara) —
/// established by the "bind" test; the frame includes the actor's yaw.</summary>
public readonly record struct Attach(int Actor, int Point)
{
    public override string ToString() => Actor == 255 ? "-" : $"actor{Actor}/pt{Point}";
}

public enum EulerOrder { XYZ, YXZ, ZXY, ZYX }

/// <summary>
/// One camera shot = one C004 entry in a CTTL timeline, plus the TMFC curve set it points to.
///
/// C004 words after the 12-byte item header (manfst00010, 104-byte variant; EW adds 6 trailing words):
///   w0 duration (frames)  w1 key  w2 TMFC id  w3 offset (unknown target)  w4 near clip  w5 far clip
///   w6/w7  root attach (actor 0xFF0000NN, point)   w8/w9 second ref (always none seen)  w10 w11
///   w12/w13 eye attach   w14/w15   w16 flag
///   w17/w18 aim attach   w19/w20   w21 flag   w22 flag
/// Rig (TMFC channels; group 8 unless noted; nodes form a parent chain):
///   g9 c116..121 node255 : lens — c116 = field of view in DEGREES (vertical vs horizontal unproven); c117 most likely
///                          roll in degrees; c118..c121 unknown (c119..121 look like depth-of-field settings)
///   g8 c0..2 node0       : unknown, always 0
///   node1 (root) c64..66 translate, c67..69 rotate (degrees), c71..73 scale  — WORLD space when the root is not attached
///   node2 (parent 1)     : same TRS layout, a second rig level
///   node3 (parent 2) c128..130 : eye offset          node4 (parent 3) c128..130 : second eye offset
///   node5 (parent 4) g9 c0..2  : up vector (0,1,0) by default
///   node6 (parent 2) c192..194 : aim (look-at) offset node7 (parent 6) c192..194 : second aim offset
/// </summary>
public sealed class CameraShot
{
    public static EulerOrder Order = EulerOrder.XYZ; // chosen by the "conv" test (Maya default rotate order)
    public static float RotationSign = 1f;

    public int Timeline, Start, Duration, TmfcId, EntrySize, Key;
    /// <summary>Position of this C004 entry among the timeline's C004 entries, in file order (the curated list's shot index).</summary>
    public int Index;
    public float Near, Far;
    public Attach Root, Eye, Aim;
    public bool IsOverlay;
    public List<CurveChannel>? Channels;

    public CurveChannel? Channel(int group, int code, int node) =>
        Channels?.FirstOrDefault(c => c.Group == group && c.Code == code && c.Node == node);

    private float V(int group, int code, int node, float frame, float fallback = 0f) =>
        Channel(group, code, node)?.Eval(frame) ?? fallback;

    public float Fov(float frame) => V(9, 116, 255, frame);

    public static IEnumerable<CameraShot> FromTimeline(CutbFile c, TmbTimeline tl)
    {
        var d = c.Data;
        var list = new List<CameraShot>();
        foreach (var it in tl.Items.Where(i => i.Magic == "C004"))
        {
            var s = new CameraShot
            {
                Timeline = tl.Index,
                Index = list.Count,
                Start = it.Time,
                EntrySize = it.Size,
                Duration = it.Word(d, 0),
                Key = it.Word(d, 1),
                TmfcId = it.Word(d, 2),
                Near = it.Float(d, 4),
                Far = it.Float(d, 5),
                Root = new Attach(it.Word(d, 6) & 0xFF, it.Word(d, 7)),
                Eye = new Attach(it.Word(d, 12) & 0xFF, it.Word(d, 13)),
                Aim = new Attach(it.Word(d, 17) & 0xFF, it.Word(d, 18)),
            };
            tl.Curves.TryGetValue(s.TmfcId, out s.Channels);
            list.Add(s);
        }
        // A shot spanning the whole timeline while other shots also exist is a second layer, not a cut.
        if (list.Count > 1)
            foreach (var s in list)
                s.IsOverlay = s.Start == 0 && s.Duration == tl.Length;
        return list;
    }

    private Matrix4x4 Rotation(int node, float frame)
    {
        var rx = V(8, 67, node, frame) * MathF.PI / 180f * RotationSign;
        var ry = V(8, 68, node, frame) * MathF.PI / 180f * RotationSign;
        var rz = V(8, 69, node, frame) * MathF.PI / 180f * RotationSign;
        var X = Matrix4x4.CreateRotationX(rx);
        var Y = Matrix4x4.CreateRotationY(ry);
        var Z = Matrix4x4.CreateRotationZ(rz);
        // Row-vector convention (v * M): the leftmost matrix is applied first.
        return Order switch
        {
            EulerOrder.XYZ => X * Y * Z,
            EulerOrder.YXZ => Z * X * Y,   // roll, then pitch, then yaw (yaw outermost)
            EulerOrder.ZXY => Y * X * Z,
            _ => Z * Y * X,
        };
    }

    private Matrix4x4 Local(int node, float frame)
    {
        if (node < 0) return Matrix4x4.Identity;
        var codes = Channels!.Where(c => c.Node == node && c.Group == 8).Select(c => c.Code).ToHashSet();
        if (codes.Contains(64))
        {
            var s = new Vector3(V(8, 71, node, frame, 1), V(8, 72, node, frame, 1), V(8, 73, node, frame, 1));
            var t = new Vector3(V(8, 64, node, frame), V(8, 65, node, frame), V(8, 66, node, frame));
            return Matrix4x4.CreateScale(s) * Rotation(node, frame) * Matrix4x4.CreateTranslation(t);
        }
        if (codes.Contains(128))
            return Matrix4x4.CreateTranslation(V(8, 128, node, frame), V(8, 129, node, frame), V(8, 130, node, frame));
        if (codes.Contains(192))
            return Matrix4x4.CreateTranslation(V(8, 192, node, frame), V(8, 193, node, frame), V(8, 194, node, frame));
        return Matrix4x4.Identity;
    }

    private int ParentOf(int node) => Channels!.FirstOrDefault(c => c.Node == node)?.Parent ?? -1;

    /// <summary>Deepest node of a chain whose channels use <paramref name="code"/> in group 8 (eye 128, aim 192).</summary>
    private int ChainEnd(int code)
    {
        var nodes = Channels!.Where(c => c.Group == 8 && c.Code == code).Select(c => (int)c.Node).Distinct().ToList();
        return nodes.FirstOrDefault(n => !nodes.Any(m => ParentOf(m) == n), -1);
    }

    private int ChainStart(int code)
    {
        var nodes = Channels!.Where(c => c.Group == 8 && c.Code == code).Select(c => (int)c.Node).Distinct().ToList();
        return nodes.FirstOrDefault(n => !nodes.Contains(ParentOf(n)), -1);
    }

    private Matrix4x4 World(int node, float frame, Func<Attach, Matrix4x4> anchor)
    {
        var parent = ParentOf(node);
        var local = Local(node, frame);
        if (parent < 0) return local * anchor(Root);
        var pw = World(parent, frame, anchor);
        // An attached aim/eye chain starts from the actor's frame instead of the rig's (offsets stay local to it).
        if (node == ChainStart(192) && Aim.Actor != 255) pw = anchor(Aim);
        if (node == ChainStart(128) && Eye.Actor != 255) pw = anchor(Eye);
        return local * pw;
    }

    /// <summary>World-space eye, look-at point, up vector and FOV (degrees) at a frame local to the shot.
    /// <paramref name="anchor"/> returns the world frame of an attach slot (identity for "none").
    /// Up: the g9 c0..2 node is read as an up-OBJECT offset in the eye's frame (default (0,1,0)); up = its world
    /// position minus the eye. This is an interpretation (values like -90 occur, so it is not a unit vector).</summary>
    public (Vector3 Eye, Vector3 Aim, Vector3 Up, float Fov) Evaluate(float frame, Func<Attach, Matrix4x4> anchor)
    {
        var eyeFrame = World(ChainEnd(128), frame, anchor);
        var eye = eyeFrame.Translation;
        var aim = World(ChainEnd(192), frame, anchor).Translation;
        var upNode = Channels!.FirstOrDefault(c => c.Group == 9 && c.Code == 1 && c.Node != 255)?.Node ?? -1;
        var up = Vector3.UnitY;
        if (upNode >= 0)
        {
            var upObj = new Vector3(V(9, 0, upNode, frame), V(9, 1, upNode, frame, 1), V(9, 2, upNode, frame));
            var u = Vector3.Transform(upObj, eyeFrame) - eye;
            if (u.LengthSquared() > 1e-8f) up = Vector3.Normalize(u);
        }
        return (eye, aim, up, Fov(frame));
    }

    /// <summary>Roll in degrees: angle of the up vector around the view axis, measured from world-up.</summary>
    public static float RollDegrees(Vector3 eye, Vector3 aim, Vector3 up)
    {
        var f = aim - eye;
        if (f.LengthSquared() < 1e-8f) return 0f;
        f = Vector3.Normalize(f);
        var right = Vector3.Cross(Vector3.UnitY, f);
        if (right.LengthSquared() < 1e-8f) return 0f;
        right = Vector3.Normalize(right);
        var level = Vector3.Cross(f, right);
        var u = up - Vector3.Dot(up, f) * f;
        if (u.LengthSquared() < 1e-8f) return 0f;
        u = Vector3.Normalize(u);
        return MathF.Atan2(Vector3.Dot(u, right), Vector3.Dot(u, level)) * 180f / MathF.PI;
    }

    public void Dump(TextWriter w, bool verbose)
    {
        w.WriteLine($"  shot CTTL#{Timeline} start {Start,4} dur {Duration,4} ({Duration / 30f:F2}s) fov {Fov(0):F1}° near {Near} far {Far} " +
                    $"root {Root} eye {Eye} aim {Aim}{(IsOverlay ? " [overlay layer]" : "")} entry {EntrySize}B");
        if (!verbose || Channels == null) return;
        foreach (var c in Channels.Where(c => c.Keys.Any(k => k.Value != 0f) && !(c.Code is 71 or 72 or 73 && c.Keys.All(k => k.Value == 1f))))
            w.WriteLine($"     g{c.Group} c{c.Code} node{c.Node} parent{c.Parent}: " +
                        string.Join(" ", c.Keys.Select(k => $"[{k.Frame:G4}: {k.Value:G5}]")));
    }
}
