// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;

namespace AdvancedRagdoll;

// Moved from the plugin's BoneTransformService.cs in stage A4 (the ragdoll's pose writer uses it).
/// <summary>Result of applying bone modifications. Contains originals and accumulated deltas for further processing.</summary>
internal class BoneModificationResult
{
    public Vector3[] OriginalPositions;
    public Quaternion[] OriginalRotations;
    public Quaternion[] AccumulatedDeltas;
    public bool[] HasAccumulated;

    public BoneModificationResult(int boneCount)
    {
        OriginalPositions = new Vector3[boneCount];
        OriginalRotations = new Quaternion[boneCount];
        AccumulatedDeltas = new Quaternion[boneCount];
        HasAccumulated = new bool[boneCount];
    }

    /// <summary>
    /// Prepare this instance for reuse across frames without reallocating.
    /// OriginalPositions/OriginalRotations are fully overwritten by the caller each frame,
    /// and AccumulatedDeltas is only read where HasAccumulated is set, so only the
    /// HasAccumulated flags need clearing. Grows the backing arrays if boneCount increased.
    /// </summary>
    public void Reset(int boneCount)
    {
        if (HasAccumulated.Length < boneCount)
        {
            OriginalPositions = new Vector3[boneCount];
            OriginalRotations = new Quaternion[boneCount];
            AccumulatedDeltas = new Quaternion[boneCount];
            HasAccumulated = new bool[boneCount];
        }
        else
        {
            Array.Clear(HasAccumulated, 0, boneCount);
        }
    }
}
