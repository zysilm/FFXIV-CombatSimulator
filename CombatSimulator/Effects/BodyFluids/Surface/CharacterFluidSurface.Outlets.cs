// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Effects.BodyFluids.Surface;

public sealed unsafe partial class CharacterFluidSurface
{
    private sealed class OutletBinding
    {
        public uint Generation;
        public Vector3 Offset, LocalTarget;
        public BodyFluidOutletKind Kind;
        public int Bone, Slot, Scan, Selected;
        public bool Eye, Complete, Bound;
        public HashSet<int> Influences = new();
        public PriorityQueue<int, float> Candidates = new();
        public int[] Triangles = Array.Empty<int>();
        public FluidSurfaceAnchor Center, Left, Right;
        public string Status = "Pending supported mesh binding";
    }
    private readonly Dictionary<BodyFluidOutletKind, OutletBinding> outletBindings = new();
    private uint outletScanFrame = uint.MaxValue, outletBindingFrame = uint.MaxValue;
    private int outletScanRemaining, outletBindingRemaining;
    private bool mouthOffsetActive;
    public string OutletStatus { get; private set; } = "No outlet queried";
    public string GetOutletStatus(BodyFluidOutletKind kind) => kind == BodyFluidOutletKind.Mouth && !mouthOffsetActive
        ? LipOutletStatus : outletBindings.TryGetValue(kind, out var binding) && binding.Generation == Generation ? binding.Status : "Not queried";

    /// <summary>Material anchors follow the final captured pose; unsupported model patches never emit in air.</summary>
    public bool TryGetOutletAnchor(BodyFluidOutletSettings settings, out FluidSurfaceAnchor anchor)
    {
        anchor = default;
        if (!settings.Enabled || !HasSurface) { OutletStatus = "Outlet disabled or pose unavailable"; return false; }
        var offset = new Vector3(settings.OffsetX, settings.OffsetY, settings.OffsetZ);
        if (settings.Kind == BodyFluidOutletKind.Mouth) mouthOffsetActive = offset != Vector3.Zero;
        if (!Finite(offset)) { OutletStatus = "Invalid outlet offset"; return false; }
        if (settings.Kind == BodyFluidOutletKind.Mouth && offset == Vector3.Zero)
        {
            bool available = TryGetMouthAnchor(out anchor);
            OutletStatus = LipOutletStatus;
            return available;
        }
        if (settings.Kind == BodyFluidOutletKind.Mouth && !TryGetMouthAnchor(out _))
        { OutletStatus = LipOutletStatus; return false; }
        if (!outletBindings.TryGetValue(settings.Kind, out var binding) || binding.Generation != Generation || binding.Offset != offset)
        {
            binding = CreateOutletBinding(settings.Kind, offset);
            outletBindings[settings.Kind] = binding;
        }
        if (binding.Bone < 0) { OutletStatus = binding.Status; return false; }
        if (!binding.Complete) ScanOutletCandidates(binding);
        if (!binding.Complete) { OutletStatus = binding.Status; return false; }
        if (!binding.Bound && !BindOutletPatch(binding)) { OutletStatus = binding.Status; return false; }
        anchor = binding.Center;
        if (binding.Eye && TryEvaluate(binding.Left, out var left) && TryEvaluate(binding.Right, out var right))
        {
            // Y is gravity's vertical in the final posed world mesh, including managed pose writers.
            float height = right.Position.Y - left.Position.Y;
            const float enter = .0015f, leave = .0006f;
            if (height > enter) binding.Selected = -1;
            else if (height < -enter) binding.Selected = 1;
            else if (Math.Abs(height) < leave) binding.Selected = 0;
            anchor = binding.Selected < 0 ? binding.Left : binding.Selected > 0 ? binding.Right : binding.Center;
        }
        if (!TryEvaluate(anchor, out _)) { OutletStatus = "Current outlet pose/query budget unavailable"; return false; }
        binding.Status = $"Bound slot={binding.Slot}; outlet={(binding.Selected < 0 ? "left" : binding.Selected > 0 ? "right" : "center")}; patch={binding.Triangles.Length}";
        OutletStatus = binding.Status;
        return true;
    }

    private OutletBinding CreateOutletBinding(BodyFluidOutletKind kind, Vector3 offset)
    {
        var result = new OutletBinding { Generation = Generation, Offset = offset, Bone = -1, Kind = kind };
        string name;
        string[] names;
        Vector3 neutral = Vector3.Zero;
        switch (kind)
        {
            case BodyFluidOutletKind.Mouth:
                name = "j_f_dlip_02_l"; names = Array.Empty<string>(); result.Slot = 11;
                foreach (int bone in allowedLipBones) result.Influences.Add(bone);
                break;
            case BodyFluidOutletKind.NoseLeft:
            case BodyFluidOutletKind.NoseRight:
                name = kind == BodyFluidOutletKind.NoseLeft ? "j_f_hana_l" : "j_f_hana_r";
                names = new[] { name }; result.Slot = 11; neutral = new(0, -.002f, 0); break;
            case BodyFluidOutletKind.EyeLeft:
            case BodyFluidOutletKind.EyeRight:
                string side = kind == BodyFluidOutletKind.EyeLeft ? "l" : "r";
                name = $"j_f_mabdn_01_{side}";
                names = new[] { name, $"j_f_mabdn_02out_{side}", $"j_f_mabdn_03in_{side}" };
                result.Slot = 11; result.Eye = true; break;
            case BodyFluidOutletKind.Part1Left:
            case BodyFluidOutletKind.Part1Right:
                name = kind == BodyFluidOutletKind.Part1Left ? "j_mune_l" : "j_mune_r";
                names = new[] { name }; result.Slot = 1; neutral = new(0, 0, .035f); break;
            case BodyFluidOutletKind.Part2:
            case BodyFluidOutletKind.Part3:
                name = "j_kosi"; names = new[] { name, "j_asi_a_l", "j_asi_a_r" }; result.Slot = 3;
                neutral = new(0, -.10f, kind == BodyFluidOutletKind.Part2 ? .07f : -.065f); break;
            default: result.Status = "Unsupported outlet kind"; return result;
        }
        result.Bone = ResolveSurfaceBone(name);
        foreach (string influence in names)
        {
            int bone = ResolveSurfaceBone(influence);
            if (bone >= 0) result.Influences.Add(bone);
        }
        if (result.Bone >= 0 && result.Slot != 11)
        {
            if (!TryOutletReferenceFrame(out var forward, out var up))
            { result.Bone = -1; result.Status = "Supported reference orientation unavailable"; return result; }
            // Translate the loaded rig's forward/up frame into this driver's local frame.
            // A local Z axis on a side driver need not point toward the visible front patch.
            neutral = Vector3.TransformNormal(forward * neutral.Z + up * neutral.Y, inverseBind[result.Bone]);
        }
        result.LocalTarget = neutral + offset;
        if (result.Bone < 0) result.Status = "Required loaded pose driver missing";
        return result;
    }

    private bool TryOutletReferenceFrame(out Vector3 forward, out Vector3 up)
    {
        forward = up = default;
        int top = ResolveSurfaceBone("j_kubi"), bottom = ResolveSurfaceBone("j_kosi");
        if (top < 0 || bottom < 0 || lowerLipLeft < 0 || lowerLipRight < 0 || upperLipLeft < 0 || upperLipRight < 0 || facialOrigin < 0) return false;
        if (!ReferenceOutletPoint(top, out var neck) || !ReferenceOutletPoint(bottom, out var center) ||
            !ReferenceOutletPoint(lowerLipLeft, out var left) || !ReferenceOutletPoint(lowerLipRight, out var right) ||
            !ReferenceOutletPoint(upperLipLeft, out var upperLeft) || !ReferenceOutletPoint(upperLipRight, out var upperRight) ||
            !ReferenceOutletPoint(facialOrigin, out var origin)) return false;
        var lipCenter = (left + right) * .5f;
        var normal = Vector3.Cross(right - left, (upperLeft + upperRight) * .5f - lipCenter);
        if (normal.LengthSquared() < 1e-14f || (neck - center).LengthSquared() < 1e-8f) return false;
        if (Vector3.Dot(normal, lipCenter - origin) < 0) normal = -normal;
        forward = Vector3.Normalize(normal); up = Vector3.Normalize(neck - center);
        return true;
    }

    private bool ReferenceOutletPoint(int bone, out Vector3 point)
    {
        point = default;
        if (!Matrix4x4.Invert(inverseBind[bone], out var transform)) return false;
        point = new(transform.M41, transform.M42, transform.M43);
        return Finite(point);
    }

    private void ScanOutletCandidates(OutletBinding binding)
    {
        if (outletScanFrame != frame) { outletScanFrame = frame; outletScanRemaining = 4096; }
        if (!Matrix4x4.Invert(inverseBind[binding.Bone], out var reference))
        { binding.Status = "Reference driver unavailable"; return; }
        var target = Vector3.Transform(binding.LocalTarget, reference);
        float radius = binding.Slot == 11 ? .04f : .18f;
        while (binding.Scan < faces.Length && outletScanRemaining > 0)
        {
            outletScanRemaining--;
            int index = binding.Scan++;
            var face = faces[index];
            if (face.Slot != binding.Slot) continue;
            var a = vertices[face.A]; var b = vertices[face.B]; var c = vertices[face.C];
            float influence = (OutletWeight(a, binding) + OutletWeight(b, binding) + OutletWeight(c, binding)) / 3;
            if (influence < .08f) continue;
            float distance = Vector3.DistanceSquared(target, (a.Position + b.Position + c.Position) / 3);
            if (distance > radius * radius) continue;
            // Negative distance keeps the least relevant member at the queue head.
            binding.Candidates.Enqueue(index, -distance);
            if (binding.Candidates.Count > 384) binding.Candidates.Dequeue();
        }
        binding.Status = $"Supported mesh scan pending {binding.Scan}/{faces.Length}";
        if (binding.Scan < faces.Length) return;
        binding.Complete = true;
        binding.Triangles = new int[binding.Candidates.Count];
        for (int i = 0; i < binding.Triangles.Length; i++) binding.Triangles[i] = binding.Candidates.Dequeue();
        binding.Status = binding.Triangles.Length == 0 ? $"No supported visible slot={binding.Slot} patch; disabled" : "Awaiting posed patch binding";
    }

    private static float OutletWeight(Vertex vertex, OutletBinding binding) =>
        (binding.Influences.Contains(vertex.B0) ? vertex.Weights.X : 0) +
        (binding.Influences.Contains(vertex.B1) ? vertex.Weights.Y : 0) +
        (binding.Influences.Contains(vertex.B2) ? vertex.Weights.Z : 0) +
        (binding.Influences.Contains(vertex.B3) ? vertex.Weights.W : 0) +
        (binding.Influences.Contains(vertex.B4) ? vertex.ExtraWeights.X : 0) +
        (binding.Influences.Contains(vertex.B5) ? vertex.ExtraWeights.Y : 0) +
        (binding.Influences.Contains(vertex.B6) ? vertex.ExtraWeights.Z : 0) +
        (binding.Influences.Contains(vertex.B7) ? vertex.ExtraWeights.W : 0);

    private bool BindOutletPatch(OutletBinding binding)
    {
        if (binding.Triangles.Length == 0) return false;
        Vector3 target = Vector3.Transform(binding.LocalTarget, boneWorld[binding.Bone]);
        if (binding.Kind == BodyFluidOutletKind.Mouth &&
            TryGetMouthAnchor(out var mouth) && TryEvaluate(mouth, out var mouthSample))
            target = mouthSample.Position + Vector3.TransformNormal(binding.Offset, boneWorld[binding.Bone]);
        if (!FindOutletAnchor(binding, target, out binding.Center)) return false;
        if (binding.Eye)
        {
            int l = ResolveSurfaceBone("j_f_dmlip_01_l"), r = ResolveSurfaceBone("j_f_dmlip_01_r");
            if (l < 0 || r < 0) { binding.Status = "Eye lateral pose frame unavailable"; return false; }
            Vector3 lateral = SafeNormal(BonePosition(r) - BonePosition(l));
            float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
            foreach (int index in binding.Triangles)
            {
                if (!OutletTriangle(index, binding, out var a, out var b, out var c))
                {
                    if (outletBindingRemaining <= 0) return false;
                    if (BudgetExhausted) { binding.Status = "Posed patch budget pending"; return false; }
                    continue;
                }
                var face = faces[index];
                for (int vertex = 0; vertex < 3; vertex++)
                {
                    int source = vertex == 0 ? face.A : vertex == 1 ? face.B : face.C;
                    if (OutletWeight(vertices[source], binding) < .10f) continue;
                    Vector3 point = vertex == 0 ? a : vertex == 1 ? b : c;
                    float coordinate = Vector3.Dot(point - target, lateral);
                    minimum = Math.Min(minimum, coordinate); maximum = Math.Max(maximum, coordinate);
                }
            }
            if (!float.IsFinite(minimum) || maximum - minimum < .003f)
            { binding.Status = "Supported lower-lid mesh span unavailable"; return false; }
            // Use real mesh span, not the near-coincident corner-driver translations.
            Vector3 leftTarget = target + lateral * (minimum + .15f * (maximum - minimum));
            Vector3 rightTarget = target + lateral * (maximum - .15f * (maximum - minimum));
            if (!FindOutletAnchor(binding, leftTarget, out binding.Left) || !FindOutletAnchor(binding, rightTarget, out binding.Right)) return false;
        }
        binding.Bound = true;
        return true;
    }

    private bool FindOutletAnchor(OutletBinding binding, Vector3 target, out FluidSurfaceAnchor anchor)
    {
        anchor = default;
        float best = binding.Slot == 11 ? .025f * .025f : .10f * .10f;
        foreach (int index in binding.Triangles)
        {
            if (!OutletTriangle(index, binding, out var a, out var b, out var c))
            {
                if (outletBindingRemaining <= 0) return false;
                if (BudgetExhausted) { binding.Status = "Posed patch budget pending"; return false; }
                continue;
            }
            Vector3 bary = ClosestTriangleBarycentric(target, a, b, c);
            float distance = Vector3.DistanceSquared(target, a * bary.X + b * bary.Y + c * bary.Z);
            if (!Finite(bary) || distance >= best) continue;
            best = distance; anchor = new(Generation, index, bary);
        }
        if (anchor.Generation == Generation) return true;
        binding.Status = "Supported patch too far from target; adjust local offset";
        return false;
    }

    private bool OutletTriangle(int index, OutletBinding binding, out Vector3 a, out Vector3 b, out Vector3 c)
    {
        a = b = c = default;
        if (outletBindingFrame != frame) { outletBindingFrame = frame; outletBindingRemaining = 2048; }
        if (outletBindingRemaining <= 0)
        { binding.Status = "Shared outlet binding budget pending"; return false; }
        outletBindingRemaining--;
        return Triangle(index, out a, out b, out c);
    }
}
