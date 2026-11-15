// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace CombatSimulator.Camera.Cutscene;

/// <summary>Bind-point table (.eid, "element id"): bind point id → bone name + offset. Layout from our own reading
/// (cross-checked against the field order VFXEditor documents, MIT): magic "eid\0", i16 version ("21" = new),
/// i16, i32 count, u32; new entries 64 B: name[32], i32 id, f32×3 position, f32×3 rotation (radians), pad.
/// Old entries 48 B: i32 id, f32×3 position, f32×3 rotation (degrees), name[12], pad.</summary>
public sealed record BindPoint(int Id, string Bone, Vector3 Offset, Vector3 Rotation);

public static class Eid
{
    public static List<BindPoint> Read(byte[] d)
    {
        var list = new List<BindPoint>();
        var isNew = BitConverter.ToInt16(d, 4) == 0x3132;
        var count = BitConverter.ToInt32(d, 8);
        var p = 16;
        for (var i = 0; i < count; i++)
        {
            if (isNew)
            {
                var name = Encoding.ASCII.GetString(d, p, 32).TrimEnd('\0');
                var nul = name.IndexOf('\0'); if (nul >= 0) name = name[..nul];
                list.Add(new BindPoint(BitConverter.ToInt32(d, p + 32),
                    name,
                    new Vector3(BitConverter.ToSingle(d, p + 36), BitConverter.ToSingle(d, p + 40), BitConverter.ToSingle(d, p + 44)),
                    new Vector3(BitConverter.ToSingle(d, p + 48), BitConverter.ToSingle(d, p + 52), BitConverter.ToSingle(d, p + 56))));
                p += 64;
            }
            else
            {
                var name = Encoding.ASCII.GetString(d, p + 28, 12);
                var nul = name.IndexOf('\0'); if (nul >= 0) name = name[..nul];
                list.Add(new BindPoint(BitConverter.ToInt32(d, p), name,
                    new Vector3(BitConverter.ToSingle(d, p + 4), BitConverter.ToSingle(d, p + 8), BitConverter.ToSingle(d, p + 12)),
                    new Vector3(BitConverter.ToSingle(d, p + 16), BitConverter.ToSingle(d, p + 20), BitConverter.ToSingle(d, p + 24))));
                p += 44;
            }
        }
        return list;
    }
}
