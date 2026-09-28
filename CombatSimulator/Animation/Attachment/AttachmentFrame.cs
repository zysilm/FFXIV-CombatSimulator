using System;
using System.Numerics;

namespace CombatSimulator.Animation.Attachment;

/// <summary>Transports the previous frame with the bone, then swings its axis toward the endpoint.
/// Unlike projecting a fresh reference axis, this remains continuous when the endpoint crosses it.</summary>
public sealed class AttachmentFrame
{
    private bool initialized;
    private Quaternion previousBone, frame;

    public Quaternion Update(Quaternion boneRotation, Vector3 direction)
    {
        boneRotation = Quaternion.Normalize(boneRotation);
        var predicted = initialized ? Quaternion.Normalize(boneRotation * Quaternion.Inverse(previousBone) * frame) : boneRotation;
        var from = Vector3.Transform(Vector3.UnitY, predicted);
        var to = AttachmentSimulation.SafeNormal(direction, from);
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        var swing = dot < -0.999999f
            ? Quaternion.CreateFromAxisAngle(Vector3.Transform(Vector3.UnitX, predicted), MathF.PI)
            : Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1f + dot));
        frame = Quaternion.Normalize(swing * predicted);
        previousBone = boneRotation;
        initialized = true;
        return frame;
    }
}
