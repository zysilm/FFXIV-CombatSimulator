// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Core;

namespace CombatSimulator.Effects.BodyFluids.Surface;

public sealed unsafe partial class CharacterFluidSurface
{
    private int[] lipCandidateTriangles = Array.Empty<int>();
    private readonly HashSet<int> allowedLipBones = new();
    private int lowerLipLeft = -1, lowerLipRight = -1, upperLipLeft = -1, upperLipRight = -1, facialOrigin = -1;
    private bool lipSelectionAttempted;
    private readonly record struct ValidatedLipProfile(ModelLoadKey Model, ulong ObjectIdentity, nint DrawIdentity, int Mesh, uint IndexEntry, int V0, int V1, int V2, Vector3 Barycentric);
    private ValidatedLipProfile? validatedLipProfile;
    /// <summary>Automatic selection is a candidate until its red marker passes the actual game lip proof.</summary>
    public bool LipAnchorIsValidated { get; private set; }

    public bool ValidateCurrentLipAnchor()
    {
        if (!TryGetMouthAnchor(out var anchor) || !TryEvaluate(anchor, out _) || faceModelKey == null || faces[anchor.Triangle].Slot != 11) return false;
        var face = faces[anchor.Triangle];
        validatedLipProfile = new(faceModelKey.Value, objectIdentity, drawIdentity, face.Mesh, face.IndexEntry, face.V0, face.V1, face.V2, anchor.Barycentric);
        LipAnchorIsValidated = true;
        LipStatus += "; current generation lip proof accepted";
        return true;
    }

    private void BuildLipCandidates()
    {
        allowedLipBones.Clear(); lipCandidateTriangles = Array.Empty<int>(); lipSelectionAttempted = false;
        // These names were observed in the current game's loaded face partial, not inferred jaw offsets.
        lowerLipLeft = ResolveSurfaceBone("j_f_dlip_02_l"); lowerLipRight = ResolveSurfaceBone("j_f_dlip_02_r");
        upperLipLeft = ResolveSurfaceBone("j_f_ulip_02_l"); upperLipRight = ResolveSurfaceBone("j_f_ulip_02_r");
        facialOrigin = ResolveSurfaceBone("j_f_face");
        if (lowerLipLeft < 0 || lowerLipRight < 0 || upperLipLeft < 0 || upperLipRight < 0 || facialOrigin < 0)
        { LipStatus = "Loaded verified lower/upper lip pair absent or ambiguous; emission disabled"; return; }
        foreach (var name in new[] { "j_f_dlip_01_l", "j_f_dlip_01_r", "j_f_dlip_02_l", "j_f_dlip_02_r",
                     "j_f_dmlip_01_l", "j_f_dmlip_01_r", "j_f_dmlip_02_l", "j_f_dmlip_02_r" })
        {
            int index = ResolveSurfaceBone(name); if (index >= 0) allowedLipBones.Add(index);
        }
        if (!Matrix4x4.Invert(inverseBind[lowerLipLeft], out var left) || !Matrix4x4.Invert(inverseBind[lowerLipRight], out var right)) return;
        var leftPoint = new Vector3(left.M41, left.M42, left.M43);
        var rightPoint = new Vector3(right.M41, right.M42, right.M43);
        var center = (leftPoint + rightPoint) * 0.5f;
        float radius = Math.Max(Vector3.Distance(leftPoint, rightPoint) * 3, 0.015f);
        var candidates = new List<(int Face, float Distance)>();
        for (int i = 0; i < faces.Length; i++)
        {
            var face = faces[i];
            if (face.Slot != 11) continue;
            var a = vertices[face.A]; var b = vertices[face.B]; var c = vertices[face.C];
            float influence = (LipWeight(a) + LipWeight(b) + LipWeight(c)) / 3;
            if (influence < 0.10f || OralInteriorWeight(a) + OralInteriorWeight(b) + OralInteriorWeight(c) > 0.05f) continue;
            float distance = Vector3.DistanceSquared(center, (a.Position + b.Position + c.Position) / 3);
            if (distance > radius * radius) continue;
            candidates.Add((i, distance));
        }
        candidates.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        int count = Math.Min(candidates.Count, 256);
        lipCandidateTriangles = new int[count];
        for (int i = 0; i < count; i++) lipCandidateTriangles[i] = candidates[i].Face;
        LipStatus = count == 0 ? "No supported visible lower-lip-weighted face patch; emission disabled" : $"Lower lip candidate patch={count} triangles; awaiting final pose / red-marker proof";
        TryRestoreValidatedLipProfile();
    }

    private void TryRestoreValidatedLipProfile()
    {
        if (validatedLipProfile == null || faceModelKey == null) return;
        var profile = validatedLipProfile.Value;
        // Scope/body changes may alter unified triangle numbers. A face resource change requires fresh proof.
        if (profile.Model != faceModelKey.Value || profile.ObjectIdentity != objectIdentity || profile.DrawIdentity != drawIdentity)
        { validatedLipProfile = null; return; }
        foreach (int index in lipCandidateTriangles)
        {
            var face = faces[index];
            // Shape changes outside this exact resolved material triangle are compatible. A changed replacement,
            // hidden face, or unsupported shape is never silently rebound to a different triangle.
            if (face.Mesh != profile.Mesh || face.IndexEntry != profile.IndexEntry || face.V0 != profile.V0 || face.V1 != profile.V1 || face.V2 != profile.V2) continue;
            lipAnchor = new(Generation, index, profile.Barycentric);
            lipBound = LipAnchorIsValidated = lipSelectionAttempted = true;
            LipStatus = $"Validated face profile restored generation={Generation} mesh={face.Mesh} indexEntry={face.IndexEntry} resolvedVertices={face.V0},{face.V1},{face.V2}";
            return;
        }
    }

    private float LipWeight(Vertex vertex) =>
        Allowed(vertex.B0, vertex.Weights.X) + Allowed(vertex.B1, vertex.Weights.Y) + Allowed(vertex.B2, vertex.Weights.Z) + Allowed(vertex.B3, vertex.Weights.W) +
        Allowed(vertex.B4, vertex.ExtraWeights.X) + Allowed(vertex.B5, vertex.ExtraWeights.Y) + Allowed(vertex.B6, vertex.ExtraWeights.Z) + Allowed(vertex.B7, vertex.ExtraWeights.W);
    private float Allowed(int bone, float weight) => allowedLipBones.Contains(bone) ? weight : 0;
    private float OralInteriorWeight(Vertex vertex) =>
        Oral(vertex.B0, vertex.Weights.X) + Oral(vertex.B1, vertex.Weights.Y) + Oral(vertex.B2, vertex.Weights.Z) + Oral(vertex.B3, vertex.Weights.W) +
        Oral(vertex.B4, vertex.ExtraWeights.X) + Oral(vertex.B5, vertex.ExtraWeights.Y) + Oral(vertex.B6, vertex.ExtraWeights.Z) + Oral(vertex.B7, vertex.ExtraWeights.W);
    private float Oral(int index, float weight) => index >= 0 && index < surfaceBoneNames.Length &&
        (surfaceBoneNames[index].StartsWith("j_f_bero_", StringComparison.Ordinal) || surfaceBoneNames[index].StartsWith("j_f_haguki", StringComparison.Ordinal)) ? weight : 0;
    private Vector3 BonePosition(int index) => new(boneWorld[index].M41, boneWorld[index].M42, boneWorld[index].M43);

    private void TrySelectLipCandidate()
    {
        if (lipBound || lipSelectionAttempted || lipCandidateTriangles.Length == 0 || !poseAvailable) return;
        var left = BonePosition(lowerLipLeft); var right = BonePosition(lowerLipRight);
        var center = (left + right) * 0.5f;
        var upper = (BonePosition(upperLipLeft) + BonePosition(upperLipRight)) * 0.5f;
        var outward = Vector3.Cross(right - left, upper - center);
        if (!Finite(outward) || outward.LengthSquared() < 1e-12f)
        { LipStatus = "Lip landmark frame degenerate; emission disabled"; lipSelectionAttempted = true; return; }
        outward = Vector3.Normalize(outward);
        if (Vector3.Dot(outward, center - BonePosition(facialOrigin)) < 0) outward = -outward;
        // A local anatomical scale bounds the projection; never search the body or use a head-offset source.
        float maxDistance = Math.Max(Vector3.Distance(left, right) * 1.5f, 0.004f);
        float bestDistance = maxDistance * maxDistance;
        int best = -1; Vector3 bestBary = default;
        foreach (int index in lipCandidateTriangles)
        {
            if (!Triangle(index, out var a, out var b, out var c))
            {
                if (BudgetExhausted) { LipStatus = "Lip candidate skin budget pending; emission disabled"; return; }
                continue;
            }
            var normal = SafeNormal(Vector3.Cross(b - a, c - a));
            if (Vector3.Dot(normal, outward) < 0.15f) continue;
            var bary = ClosestTriangleBarycentric(center, a, b, c);
            var point = a * bary.X + b * bary.Y + c * bary.Z;
            float distance = Vector3.DistanceSquared(center, point);
            if (!Finite(bary) || distance >= bestDistance) continue;
            bestDistance = distance; best = index; bestBary = bary;
        }
        lipSelectionAttempted = true;
        if (best < 0) { LipStatus = "No outward local lower-lip triangle within landmark distance; emission disabled"; return; }
        lipAnchor = new(Generation, best, bestBary); lipBound = true; LipAnchorIsValidated = false;
        var face = faces[best];
        LipStatus = $"AUTO LIP CANDIDATE UNVERIFIED generation={Generation} slot={face.Slot} mesh={face.Mesh} indexEntry={face.IndexEntry} " +
            $"resolvedVertices={face.V0},{face.V1},{face.V2} bary={bestBary} boneProjection={MathF.Sqrt(bestDistance) * 1000:0.000} mm; emission disabled until red-marker proof";
        Services.Log.Info($"Body fluid lip: {LipStatus}");
    }

    // Closest point on the triangle's face/edges/vertices (material barycentric coordinates).
    private static Vector3 ClosestTriangleBarycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return Vector3.UnitX;
        var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return Vector3.UnitY;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { float v = d1 / (d1 - d3); return new(1 - v, v, 0); }
        var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return Vector3.UnitZ;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { float w = d2 / (d2 - d6); return new(1 - w, 0, w); }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) { float w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return new(0, 1 - w, w); }
        float denominator = 1 / (va + vb + vc); float y = vb * denominator, z = vc * denominator;
        return new(1 - y - z, y, z);
    }
}
