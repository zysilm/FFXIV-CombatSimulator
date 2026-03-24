using System;
using System.Numerics;

namespace CombatSimulator.Physics;

/// <summary>
/// Captures the death pose after the animation settles, and estimates the floor plane.
/// This becomes the "rest state" that the ragdoll simulation deviates from and springs back toward.
/// </summary>
public class DeathPoseCapture
{
    /// <summary>The captured bone snapshot (local rotations, model positions, hierarchy).</summary>
    public BoneSnapshot Snapshot { get; }

    /// <summary>Estimated floor Y position in model space.</summary>
    public float FloorY { get; }

    /// <summary>Pose orientation relative to floor.</summary>
    public DeathPoseOrientation Orientation { get; }

    /// <summary>Indices of bones eligible for ragdoll physics (excludes root, face, fingers).</summary>
    public int[] RagdollBoneIndices { get; }

    /// <summary>Map from bone index to its children.</summary>
    public int[][] ChildrenMap { get; }

    public DeathPoseCapture(BoneSnapshot snapshot)
    {
        Snapshot = snapshot;
        FloorY = EstimateFloorY(snapshot);
        Orientation = DetectOrientation(snapshot);
        RagdollBoneIndices = BuildRagdollBoneSet(snapshot);
        ChildrenMap = BuildChildrenMap(snapshot);
    }

    private static float EstimateFloorY(BoneSnapshot snapshot)
    {
        // Find the lowest model-space Y among all bones — that's approximately the floor.
        float minY = float.MaxValue;
        for (int i = 0; i < snapshot.BoneCount; i++)
        {
            var y = snapshot.ModelPositions[i].Y;
            if (y < minY)
                minY = y;
        }
        return minY;
    }

    private static DeathPoseOrientation DetectOrientation(BoneSnapshot snapshot)
    {
        // Find spine and head bones by name to determine orientation
        int spineIdx = -1, headIdx = -1;
        for (int i = 0; i < snapshot.BoneCount; i++)
        {
            var name = snapshot.BoneNames[i];
            if (name == "j_sebo_a" && spineIdx < 0) spineIdx = i;
            if (name == "j_kao" && headIdx < 0) headIdx = i;
        }

        if (spineIdx < 0 || headIdx < 0)
            return DeathPoseOrientation.Unknown;

        // Compare head Y vs spine Y — if head is higher, likely face-up or face-down
        var spinePos = snapshot.ModelPositions[spineIdx];
        var headPos = snapshot.ModelPositions[headIdx];
        var headToSpine = headPos - spinePos;

        // If the vertical difference is small, character is on their side
        if (MathF.Abs(headToSpine.Y) < 0.1f)
            return DeathPoseOrientation.Side;

        // Check if head/spine direction is mostly horizontal (lying flat)
        var horizontalDist = MathF.Sqrt(headToSpine.X * headToSpine.X + headToSpine.Z * headToSpine.Z);
        if (horizontalDist > MathF.Abs(headToSpine.Y) * 2f)
        {
            // Lying flat — check spine direction relative to floor to guess face-up/down
            // This is approximate; the key behavior is the same regardless
            return headToSpine.Y > 0 ? DeathPoseOrientation.FaceUp : DeathPoseOrientation.FaceDown;
        }

        return DeathPoseOrientation.Unknown;
    }

    private static int[] BuildRagdollBoneSet(BoneSnapshot snapshot)
    {
        // Include bones that make sense for ragdoll: major body bones, exclude root, face details, fingers
        var eligible = new System.Collections.Generic.List<int>();

        for (int i = 0; i < snapshot.BoneCount; i++)
        {
            var name = snapshot.BoneNames[i];

            // Skip root bone (index 0)
            if (i == 0) continue;

            // Skip face bones (j_f_* prefix)
            if (name.StartsWith("j_f_")) continue;

            // Skip finger bones (j_hte_, j_oya_, j_hito_, j_naka_, j_kus_, j_ko_)
            if (name.StartsWith("j_hte_") || name.StartsWith("j_oya_") ||
                name.StartsWith("j_hito_") || name.StartsWith("j_naka_") ||
                name.StartsWith("j_kus_") || name.StartsWith("j_ko_")) continue;

            // Skip EX bones (extra/accessory)
            if (name.StartsWith("j_ex_")) continue;

            // Skip upper body core — head, neck, spine rotate awkwardly without translation
            // and cause rubber-gum artifacts at the neck-head joint
            if (name == "j_kao" || name == "j_kubi" ||
                name == "j_sebo_a" || name == "j_sebo_b" || name == "j_sebo_c") continue;

            // Include everything else (clavicles, limbs, pelvis, etc.)
            eligible.Add(i);
        }

        return eligible.ToArray();
    }

    private static int[][] BuildChildrenMap(BoneSnapshot snapshot)
    {
        var childLists = new System.Collections.Generic.List<int>[snapshot.BoneCount];
        for (int i = 0; i < snapshot.BoneCount; i++)
            childLists[i] = new System.Collections.Generic.List<int>();

        for (int i = 0; i < snapshot.BoneCount; i++)
        {
            var parent = snapshot.ParentIndices[i];
            if (parent >= 0 && parent < snapshot.BoneCount)
                childLists[parent].Add(i);
        }

        var result = new int[snapshot.BoneCount][];
        for (int i = 0; i < snapshot.BoneCount; i++)
            result[i] = childLists[i].ToArray();

        return result;
    }
}

public enum DeathPoseOrientation
{
    Unknown,
    FaceUp,
    FaceDown,
    Side,
}
