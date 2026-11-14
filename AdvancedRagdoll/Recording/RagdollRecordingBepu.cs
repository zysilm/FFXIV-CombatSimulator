// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Reflection;
using BepuPhysics;

namespace CombatSimulator.Animation.Recording;

/// <summary>The parts of the recorder that read a BepuPhysics simulation.</summary>
public static class RagdollRecordingBepu
{
    /// <summary>Version of the BepuPhysics assembly in use (informational version when present).</summary>
    public static string BepuVersion()
    {
        var assembly = typeof(Simulation).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return $"{assembly.GetName().Version} ({informational ?? "no informational version"})";
    }

    /// <summary>
    /// Writes one <see cref="RecordType.BodyStates"/> record: every body of the simulation (awake,
    /// asleep, dynamic and kinematic) in ascending handle order.
    /// </summary>
    public static void WriteBodyStates(RagdollRecordingWriter writer, Simulation simulation, int tick, int physicsFrame,
        int substep, bool steppedExternally, ref int[] handleScratch, ref byte[] flagScratch, ref float[] stateScratch)
    {
        var bodies = simulation.Bodies;
        var highest = bodies.HandlePool.HighestPossiblyClaimedId;
        var capacity = Math.Max(0, highest + 1);
        if (handleScratch.Length < capacity)
        {
            handleScratch = new int[capacity * 2];
            flagScratch = new byte[capacity * 2];
            stateScratch = new float[capacity * 2 * RagdollRecordingFormat.BodyStateFloats];
        }

        var count = 0;
        for (var value = 0; value <= highest; value++)
        {
            var handle = new BodyHandle(value);
            if (!bodies.BodyExists(handle)) continue;
            var body = bodies.GetBodyReference(handle);
            ref var pose = ref body.Pose;
            ref var velocity = ref body.Velocity;
            handleScratch[count] = value;
            flagScratch[count] = (byte)((body.Awake ? 1 : 0) | (body.Kinematic ? 2 : 0));
            var at = count * RagdollRecordingFormat.BodyStateFloats;
            stateScratch[at] = pose.Position.X;
            stateScratch[at + 1] = pose.Position.Y;
            stateScratch[at + 2] = pose.Position.Z;
            stateScratch[at + 3] = pose.Orientation.X;
            stateScratch[at + 4] = pose.Orientation.Y;
            stateScratch[at + 5] = pose.Orientation.Z;
            stateScratch[at + 6] = pose.Orientation.W;
            stateScratch[at + 7] = velocity.Linear.X;
            stateScratch[at + 8] = velocity.Linear.Y;
            stateScratch[at + 9] = velocity.Linear.Z;
            stateScratch[at + 10] = velocity.Angular.X;
            stateScratch[at + 11] = velocity.Angular.Y;
            stateScratch[at + 12] = velocity.Angular.Z;
            count++;
        }

        using (writer.Begin(RecordType.BodyStates))
        {
            writer.I32(tick);
            writer.I32(physicsFrame);
            writer.I32(substep);
            writer.Bool(steppedExternally);
            writer.Ints(handleScratch.AsSpan(0, count));
            writer.Bytes(flagScratch.AsSpan(0, count));
            writer.Floats(stateScratch.AsSpan(0, count * RagdollRecordingFormat.BodyStateFloats));
        }
    }
}
