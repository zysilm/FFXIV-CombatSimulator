using System.Numerics;
using CombatSimulator;
using CombatSimulator.Effects.BodyFluids.Surface;

int assertions = 0;
void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
foreach (bool custom in new[] { true, false })
    foreach (var pose in new[] {
        Matrix4x4.Identity,
        Matrix4x4.CreateRotationX(1.2f) * Matrix4x4.CreateRotationZ(.7f) * Matrix4x4.CreateTranslation(4, .2f, -3),
        Matrix4x4.CreateScale(1.3f, .9f, 1.1f) * Matrix4x4.CreateRotationY(.8f) * Matrix4x4.CreateTranslation(2, 0, 1) })
    {
        var surface = new CharacterFluidSurface(custom, pose);
        Check(surface.HasBone(custom ? "iv_c_mune_l" : "j_mune_l"), "Expected loaded profile driver absent");
        Check(!custom || !surface.HasBone("j_mune_l"), "Custom fixture must omit stock driver");
        foreach (var kind in new[] { BodyFluidOutletKind.Part1Left, BodyFluidOutletKind.Part1Right })
        {
            bool left = kind == BodyFluidOutletKind.Part1Left;
            Check(surface.TryGetOutletAnchor(new() { Kind = kind }, out var anchor), $"Bind failed: {kind} {surface.OutletStatus}");
            Check(anchor.Triangle == (left ? 1 : 4), $"Selected internal/back or opposite side patch: {kind} triangle={anchor.Triangle}");
            Check(surface.TryEvaluate(anchor, out var sample), "Bound anchor did not evaluate");
            Check(Matrix4x4.Invert(pose, out var inverse), "Fixture pose not invertible");
            var reference = Vector3.Transform(sample.Position, inverse);
            Check(Math.Abs(reference.Z - .145f) < 1e-5f, "Outer patch beyond old 100mm cutoff not selected");
            Check(Math.Abs(reference.X - (left ? -.15f : .15f)) < 1e-5f, "Source crossed to opposite site");
            surface.SetPose(pose);
            Check(surface.TryGetOutletAnchor(new() { Kind = kind }, out var rebound) && rebound.Triangle == anchor.Triangle,
                "Material anchor changed on subsequent pose capture");
        }
        Check(surface.TryGetOutletAnchor(new() { Kind = BodyFluidOutletKind.Mouth }, out var mouth), "Mouth delegation failed");
        Check(mouth.Triangle == 99 && mouth.Barycentric == new Vector3(.2f, .3f, .5f) && surface.MouthQueries == 1,
            "Zero-offset mouth anchor/delegation behavior changed");
    }
FluidSurfaceAnchor? referenceLip = null;
foreach (var pose in new[] { Matrix4x4.Identity, Matrix4x4.CreateRotationZ(1.4f) * Matrix4x4.CreateTranslation(4, .1f, -2) })
    foreach (bool changedFacialPose in new[] { false, true })
    {
        var surface = new CharacterFluidSurface(true, pose);
        var lip = surface.BindLipFixture(changedFacialPose);
        Check(lip.Triangle == 1, "Lip binding selected lower bulge or inward-facing sheet instead of supporting edge");
        Check(surface.TryEvaluate(lip, out var sample), "Bound lip cannot evaluate");
        Matrix4x4.Invert(pose, out var inverse);
        var point = Vector3.Transform(sample.Position, inverse);
        Check(Math.Abs(point.Y - 1.505f) < 1e-5f && Math.Abs(point.Z - .102f) < 1e-5f,
            "Lip source does not lie on lower lip's upper supporting edge");
        if (referenceLip is { } prior)
            Check(lip.Triangle == prior.Triangle && Vector3.Distance(lip.Barycentric, prior.Barycentric) < 1e-6f,
                "Initial facial/body pose altered selected lip material anchor");
        referenceLip = lip;
    }
Console.WriteLine($"PASS: {assertions} assertions; production outlet/lip binding, custom/stock drivers, outer/internal/back patches, posed/nonuniform transforms, Mouth delegation and reference lip-edge pose independence.");
