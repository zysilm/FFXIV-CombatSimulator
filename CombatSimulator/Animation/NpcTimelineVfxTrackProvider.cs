// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Animation;

[Flags]
public enum NpcTimelineVfxRole
{
    Unknown = 0,
    Attack = 1,
    Impact = 2,
    Projectile = 4,
}

public readonly record struct NpcTimelineVfxEvent(
    string Path,
    float DelaySeconds,
    float LifetimeSeconds,
    NpcTimelineVfxRole Role);

/// <summary>
/// Reads the VFX commands authored inside an action timeline TMB. The TMB supplies the trigger
/// frame; the AVFX binder graph supplies the semantic role. Point binders identify caster/target
/// effects, while a linear/spline binder from caster to target identifies an in-flight effect.
/// </summary>
public sealed class NpcTimelineVfxTrackProvider
{
    private const float FramesPerSecond = 30f;
    private const float MinimumLifetimeSeconds = 2.5f;
    private const float LifetimeGraceSeconds = 1f;
    private const float MaximumLifetimeSeconds = 15f;

    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Dictionary<string, NpcTimelineVfxEvent[]> trackCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NpcTimelineVfxRole> roleCache =
        new(StringComparer.OrdinalIgnoreCase);

    public NpcTimelineVfxTrackProvider(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public IReadOnlyList<NpcTimelineVfxEvent> GetTrack(string tmbPath)
    {
        if (string.IsNullOrWhiteSpace(tmbPath))
            return Array.Empty<NpcTimelineVfxEvent>();
        if (trackCache.TryGetValue(tmbPath, out var cached))
            return cached;

        var parsed = ParseTrack(tmbPath);
        trackCache[tmbPath] = parsed;
        return parsed;
    }

    private NpcTimelineVfxEvent[] ParseTrack(string tmbPath)
    {
        try
        {
            var data = dataManager.GetFile(tmbPath)?.Data;
            if (data == null || data.Length < 12 ||
                data[0] != (byte)'T' || data[1] != (byte)'M' ||
                data[2] != (byte)'L' || data[3] != (byte)'B')
                return Array.Empty<NpcTimelineVfxEvent>();

            var itemCount = ReadInt32(data, 8);
            if (itemCount <= 0)
                return Array.Empty<NpcTimelineVfxEvent>();

            var events = new List<NpcTimelineVfxEvent>();
            var offset = 12;
            for (var itemIndex = 0; itemIndex < itemCount && offset + 12 <= data.Length; itemIndex++)
            {
                var size = ReadInt32(data, offset + 4);
                if (size < 8 || offset + size > data.Length)
                    break;

                var isVfx = HasMagic(data, offset, "C012");
                var isAsyncVfx = HasMagic(data, offset, "C173");
                if ((isVfx || isAsyncVfx) && offset + 24 <= data.Length)
                {
                    var frame = Math.Max(0, (int)ReadInt16(data, offset + 10));
                    var durationFrames = isVfx ? Math.Max(0, ReadInt32(data, offset + 12)) : 0;
                    var pathRelative = ReadInt32(data, offset + 20);
                    var pathOffset = (long)offset + 8L + pathRelative;
                    var path = ReadVfxPath(data, pathOffset);
                    if (path != null)
                    {
                        var role = ResolveRole(path);
                        var lifetime = Math.Clamp(
                            durationFrames / FramesPerSecond + LifetimeGraceSeconds,
                            MinimumLifetimeSeconds,
                            MaximumLifetimeSeconds);
                        events.Add(new NpcTimelineVfxEvent(
                            path,
                            frame / FramesPerSecond,
                            lifetime,
                            role));
                    }
                }

                offset += size;
            }

            if (events.Count == 0)
                return Array.Empty<NpcTimelineVfxEvent>();

            events.Sort(static (left, right) => left.DelaySeconds.CompareTo(right.DelaySeconds));
            var attackCount = 0;
            var impactCount = 0;
            var projectileCount = 0;
            foreach (var timelineEvent in events)
            {
                if ((timelineEvent.Role & NpcTimelineVfxRole.Attack) != 0) attackCount++;
                if ((timelineEvent.Role & NpcTimelineVfxRole.Impact) != 0) impactCount++;
                if ((timelineEvent.Role & NpcTimelineVfxRole.Projectile) != 0) projectileCount++;
            }

            log.Info(
                $"NPC timeline VFX track: '{tmbPath}', events={events.Count}, " +
                $"attack={attackCount}, impact={impactCount}, projectile={projectileCount}.");
            return events.ToArray();
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to parse NPC timeline VFX track '{tmbPath}'.");
            return Array.Empty<NpcTimelineVfxEvent>();
        }
    }

    private NpcTimelineVfxRole ResolveRole(string avfxPath)
    {
        if (roleCache.TryGetValue(avfxPath, out var cached))
            return cached;

        var role = NpcTimelineVfxRole.Unknown;
        try
        {
            var data = dataManager.GetFile(avfxPath)?.Data;
            if (data == null || data.Length < 8)
                return CacheRole(avfxPath, role);

            var rootSize = ReadInt32(data, 4);
            if (rootSize <= 0 || rootSize > data.Length - 8)
                return CacheRole(avfxPath, role);

            foreach (var rootChild in ReadChildren(data, 8, rootSize))
            {
                if (!rootChild.Name.Equals("Bind", StringComparison.Ordinal))
                    continue;

                var binderType = -1;
                int? startPoint = null;
                int? goalPoint = null;
                foreach (var binderChild in ReadChildren(data, rootChild.ContentOffset, rootChild.Size))
                {
                    if (binderChild.Name == "BnVr" && binderChild.Size >= 4)
                    {
                        binderType = ReadInt32(data, binderChild.ContentOffset);
                        continue;
                    }

                    if (binderChild.Name is not ("PrpS" or "PrpG"))
                        continue;

                    foreach (var property in ReadChildren(data, binderChild.ContentOffset, binderChild.Size))
                    {
                        if (property.Name != "BPT" || property.Size < 4)
                            continue;
                        var bindPoint = ReadInt32(data, property.ContentOffset);
                        if (binderChild.Name == "PrpS") startPoint = bindPoint;
                        else goalPoint = bindPoint;
                        break;
                    }
                }

                // AVFX binder points: 0 = caster, 1 = target. Linear, spline, and
                // linear-adjust binders connecting the two are the projectile/beam path itself.
                if (binderType is 1 or 2 or 4 && startPoint == 0 && goalPoint == 1)
                    role |= NpcTimelineVfxRole.Projectile;
                else if (startPoint == 0)
                    role |= NpcTimelineVfxRole.Attack;
                else if (startPoint == 1)
                    role |= NpcTimelineVfxRole.Impact;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to classify NPC timeline AVFX '{avfxPath}'.");
        }

        return CacheRole(avfxPath, role);
    }

    private NpcTimelineVfxRole CacheRole(string path, NpcTimelineVfxRole role)
    {
        roleCache[path] = role;
        return role;
    }

    private static IEnumerable<AvfxChild> ReadChildren(byte[] data, int offset, int size)
    {
        var endLong = (long)offset + size;
        if (offset < 0 || size < 0 || endLong > data.Length)
            yield break;
        var end = (int)endLong;

        while (offset + 8 <= end)
        {
            var nameBytes = data.AsSpan(offset, 4).ToArray();
            Array.Reverse(nameBytes);
            var name = Encoding.ASCII.GetString(nameBytes).Trim('\0');
            var contentSize = ReadInt32(data, offset + 4);
            var contentOffset = offset + 8;
            if (contentSize < 0 || (long)contentOffset + contentSize > end)
                yield break;

            yield return new AvfxChild(name, contentOffset, contentSize);
            var padding = (4 - contentSize % 4) % 4;
            offset = contentOffset + contentSize + padding;
        }
    }

    private static string? ReadVfxPath(byte[] data, long pathOffset)
    {
        if (pathOffset < 0 || pathOffset >= data.Length)
            return null;

        var start = (int)pathOffset;
        var end = start;
        while (end < data.Length && data[end] != 0)
            end++;
        if (end == data.Length || end == start || end - start > 512)
            return null;

        var path = Encoding.UTF8.GetString(data, start, end - start);
        return path.StartsWith("vfx/", StringComparison.OrdinalIgnoreCase) &&
               path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }

    private static bool HasMagic(byte[] data, int offset, string magic)
        => offset >= 0 && offset + 4 <= data.Length &&
           data[offset] == magic[0] && data[offset + 1] == magic[1] &&
           data[offset + 2] == magic[2] && data[offset + 3] == magic[3];

    private static short ReadInt16(byte[] data, int offset)
        => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, sizeof(short)));

    private static int ReadInt32(byte[] data, int offset)
        => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(int)));

    private readonly record struct AvfxChild(string Name, int ContentOffset, int Size);
}
