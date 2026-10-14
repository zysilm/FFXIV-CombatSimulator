using System.Numerics;
using CombatSimulator.Animation;
using Lumina.Data.Parsing;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
void Near(Vector3 actual, Vector3 expected, string message) => Check(Vector3.Distance(actual, expected) < 1e-5f, message + $": {actual} != {expected}");
var bytes = new byte[4 * 28 + 6];
for (int i = 0; i < 4; i++)
{
    var p = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }[i];
    BitConverter.GetBytes(p.X).CopyTo(bytes, i * 28); BitConverter.GetBytes(p.Y).CopyTo(bytes, i * 28 + 4); BitConverter.GetBytes(p.Z).CopyTo(bytes, i * 28 + 8);
    bytes[i * 28 + 12] = 128; bytes[i * 28 + 19] = 127;
    bytes[i * 28 + 20] = 0; bytes[i * 28 + 27] = 1;
}
bytes[112] = 0; bytes[114] = 1; bytes[116] = 2;
var data = new ModelGeometryData
{
    Data = bytes,
    FileHeader = new() { VertexOffset = new uint[] { 0 }, IndexOffset = new uint[] { 112 } },
    Meshes = new[] { new MdlStructs.MeshStruct { VertexCount = 4, IndexCount = 3, StartIndex = 0, SubMeshCount = 1, SubMeshIndex = 0,
        VertexBufferOffset = new uint[] { 0 }, VertexBufferStride = new byte[] { 28 } } },
    VertexDeclarations = new[] { new MdlStructs.VertexDeclarationStruct { VertexElements = new[] {
        new MdlStructs.VertexElement { Stream = 0, Offset = 0, Usage = 0, Type = 2 },
        new MdlStructs.VertexElement { Stream = 0, Offset = 12, Usage = 1, Type = 17 },
        new MdlStructs.VertexElement { Stream = 0, Offset = 20, Usage = 2, Type = 17 } } } },
    Submeshes = new[] { new MdlStructs.SubmeshStruct { IndexOffset = 0, IndexCount = 3, AttributeIndexMask = 2 } },
    Shapes = new[] { new MdlStructs.ShapeStruct { ShapeMeshStartIndex = new ushort[] { 0 }, ShapeMeshCount = new ushort[] { 1 } } },
    ShapeMeshes = new[] { new MdlStructs.ShapeMeshStruct { MeshIndexOffset = 0, ShapeValueOffset = 0, ShapeValueCount = 1 } },
    ShapeValues = new[] { new MdlStructs.ShapeValueStruct { BaseIndicesIndex = 2, ReplacingVertexIndex = 3 } },
};
var vertices = new List<RenderedGeometryVertex>(); var triangles = new List<int>();
var skin = new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, 2, 0) };
var world = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(5, 6, 7);
int Append(uint attributes, bool shape, int[]? map = null) => RenderedGeometryDecoder.Append(data, 0, 0, 1, attributes,
    new[] { shape }, map ?? new[] { 0, 1 }, skin, null, world, vertices, triangles);
Check(Append(0, false) == 0 && vertices.Count == 0, "Hidden submesh leaked into capture");
Check(Append(2, false) == 1, "Visible triangle missing");
Near(vertices[0].WorldPosition, new(5, 6 + 3 * 254f / 255, 7), "Eighth influence or full actor scale lost");
Check(vertices[0].Influences.Length == 2 && vertices[0].SourceSlot == 1, "Influence/source metadata lost");
Near(vertices[0].Influences.Aggregate(Vector3.Zero, (sum, influence) => sum + influence.WorldContribution * influence.Weight), vertices[0].WorldPosition, "Per-influence world contributions do not reconstruct captured vertex");
vertices.Clear(); triangles.Clear();
Check(Append(2, true) == 1, "Active shape removed valid triangle");
Near(vertices[2].WorldPosition, new(5, 6 + 3 * 254f / 255, 11), "Active shape did not replace indexed vertex");
vertices.Clear(); triangles.Clear();
Check(Append(2, false, new[] { 0, -1 }) == 0, "Unresolved weighted bones silently fell back to bind pose");
data.ShapeValues[0].ReplacingVertexIndex = 99;
Check(Append(2, true) == 0, "Malformed shape replacement accepted");
Check(RenderedGeometryDecoder.StringAt(System.Text.Encoding.UTF8.GetBytes("角色\0"), 0) == "角色", "UTF8 name corrupted");
Check(RenderedGeometryDecoder.StringAt(Array.Empty<byte>(), 2) == "", "Invalid string offset accepted");

byte[] Pbd()
{
    using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
    writer.Write(2);
    writer.Write((ushort)101); writer.Write((short)0); writer.Write(0); writer.Write(1f);
    writer.Write((ushort)201); writer.Write((short)1); writer.Write(44); writer.Write(1f);
    writer.Write((short)-1); writer.Write((short)1); writer.Write((short)-1); writer.Write((short)0);
    writer.Write((short)0); writer.Write((short)-1); writer.Write((short)-1); writer.Write((short)1);
    writer.Write(1); writer.Write((ushort)56); writer.Write((ushort)0);
    foreach (float f in new float[] { 2, 0, 0, 3, 0, 1, 0, 4, 0, 0, 1, 5 }) writer.Write(f);
    writer.Write(System.Text.Encoding.UTF8.GetBytes("root\0")); return stream.ToArray();
}
var pbd = new RenderedRaceDeformation(Pbd());
Check(pbd.TryBuild(101, 201, new[] { "root", "child" }, new[] { -1, 0 }, out var matrices), "PBD parent-to-child route failed");
Near(Vector3.Transform(Vector3.One, matrices[0]), new(5, 5, 6), "PBD matrix transpose/translation wrong");
Near(Vector3.Transform(Vector3.One, matrices[1]), new(5, 5, 6), "PBD parent bone fallback missing");
Check(!pbd.TryBuild(201, 101, new[] { "root" }, new[] { -1 }, out _), "Unsupported reverse PBD route silently accepted");
byte[] ThreeLevelPbd()
{
    using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
    writer.Write(3);
    for (int i = 0; i < 3; i++)
    { writer.Write((ushort)(101 + i * 100)); writer.Write((short)i); writer.Write(i == 0 ? 0 : i == 1 ? 64 : 128); writer.Write(1f); }
    for (int i = 0; i < 3; i++)
    { writer.Write((short)(i - 1)); writer.Write((short)(i == 2 ? -1 : i + 1)); writer.Write((short)-1); writer.Write((short)i); }
    for (int i = 1; i < 3; i++)
    {
        stream.Position = i == 1 ? 64 : 128;
        writer.Write(1); writer.Write((ushort)56); writer.Write((ushort)0);
        foreach (float f in new float[] { i == 1 ? 2 : 1, 0, 0, i == 1 ? 3 : 1, 0, 1, 0, 0, 0, 0, 1, 0 }) writer.Write(f);
        writer.Write(System.Text.Encoding.UTF8.GetBytes("root\0"));
    }
    return stream.ToArray();
}
var three = new RenderedRaceDeformation(ThreeLevelPbd());
Check(three.TryBuild(101, 301, new[] { "root" }, new[] { -1 }, out var threeMatrices), "Multi-step PBD route failed");
Near(Vector3.Transform(Vector3.One, threeMatrices[0]), new(7, 1, 1), "PBD noncommuting composition order wrong");
vertices.Clear(); triangles.Clear();
data.ShapeValues[0].ReplacingVertexIndex = 3;
var mirrored = new List<RenderedGeometryVertex>();
Check(RenderedGeometryDecoder.Append(data, 0, 0, 1, 2, new[] { false }, new[] { 0, 1 }, skin, null,
    Matrix4x4.CreateScale(-2, 3, 4), mirrored, triangles) == 1, "Negative-scale triangle omitted");
Near(mirrored[1].WorldPosition, new(-2, 3 * 254f / 255, 0), "Negative nonuniform scale lost");
bool rejected = false;
try { new RenderedRaceDeformation(new byte[4]); } catch (InvalidDataException) { rejected = true; }
Check(rejected, "Malformed PBD accepted");
var partialReference = new[] { Matrix4x4.CreateTranslation(1, 0, 0), Matrix4x4.CreateTranslation(1, 2, 0) };
var primaryRef = Matrix4x4.CreateTranslation(0, 10, 0);
Check(RenderedPartialGeometry.TrySkinMatrices(partialReference, partialReference, 0, primaryRef, primaryRef, out var partialRest), "Partial reference connection rejected");
Near(Vector3.Transform(new(0, 12, 0), partialRest[1]), new(0, 12, 0), "Attached partial bind pose moved authored geometry");
var partialCurrent = new[] { Matrix4x4.CreateTranslation(100, 0, 0), Matrix4x4.CreateTranslation(100, 3, 0) };
Check(RenderedPartialGeometry.TrySkinMatrices(partialReference, partialCurrent, 0, primaryRef,
    Matrix4x4.CreateTranslation(0, 20, 0), out var partialAnimated), "Partial animated connection rejected");
Near(Vector3.Transform(new(0, 12, 0), partialAnimated[1]), new(0, 23, 0), "Partial current connection or local animation offset lost");
Near(Vector3.Transform(new(0, 10, 0), partialAnimated[0]), new(0, 20, 0), "Connected bone does not track primary anchor");
Check(!RenderedPartialGeometry.TrySkinMatrices(partialReference, partialCurrent, 9, primaryRef, primaryRef, out _), "Invalid partial connection accepted");
vertices.Clear(); triangles.Clear();
Check(RenderedGeometryDecoder.Append(data, 0, 0, 11, 2, new[] { false }, new[] { 0, 1 }, partialAnimated, null,
    Matrix4x4.Identity, vertices, triangles, new[] { 7, 7 }) == 1, "Partial palette decoder dropped valid face");
Check(vertices[0].Influences.All(i => i.BoneIndex == 7), "Partial influence ownership was not remapped to primary anchor");
Near(vertices[0].Influences.Aggregate(Vector3.Zero, (sum, i) => sum + i.WorldContribution * i.Weight), vertices[0].WorldPosition,
    "Remapped partial influences lost original animated skin contribution");
// Enforce the cap before appending another face; do not first accumulate a huge capture.
var full = Enumerable.Repeat(0, RenderedGeometryDecoder.MaximumSourceFaces * 3).ToList();
bool capped = false;
try { RenderedGeometryDecoder.Append(data, 0, 0, 1, 2, new[] { false }, new[] { 0, 1 }, skin, null, Matrix4x4.Identity, new(), full); }
catch (InvalidDataException) { capped = true; }
Check(capped && full.Count == RenderedGeometryDecoder.MaximumSourceFaces * 3, "Source face cap was enforced after list growth");
Console.WriteLine($"Rendered geometry: {checks} checks passed.");
if (args.Length > 0) OfflineFixtures.Run(args[0]);
