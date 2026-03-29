using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace CombatSimulator.Animation;

/// <summary>
/// Native struct for ActionTimeline animation parameters (0x60 bytes).
/// Ported from BypassEmote's ActionTimelineAnimParams.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x60)]
public struct ActionTimelineAnimParams
{
    [FieldOffset(0x00)] public nint VtblAddr;
    [FieldOffset(0x10)] public float Unk0;
    [FieldOffset(0x14)] public float Unk4;
    [FieldOffset(0x18)] public float Unk8;
    [FieldOffset(0x1C)] public float UnkC;
    [FieldOffset(0x20)] public float Unk10;
    [FieldOffset(0x24)] public float Intensity;
    [FieldOffset(0x28)] public float StartTS;
    [FieldOffset(0x2C)] public float Unk1C;
    [FieldOffset(0x30)] public ulong Unk20;
    [FieldOffset(0x38)] public ulong TargetObjId;
    [FieldOffset(0x40)] public uint Unk30;
    [FieldOffset(0x44)] public uint Priority;
    [FieldOffset(0x48)] public int Unk38;
    [FieldOffset(0x4C)] public byte Unk3C;
    [FieldOffset(0x4D)] public byte Unk3D;
    [FieldOffset(0x4E)] public byte Unk3E;
    [FieldOffset(0x4F)] public byte Unk3F;
    [FieldOffset(0x50)] public byte Unk40;
    [FieldOffset(0x52)] public byte Unk42;
}

/// <summary>
/// Plays emote animations on any Character* using ActionTimeline, bypassing emote unlock checks.
/// Ported from BypassEmote's ActionTimelinePlayer approach.
/// </summary>
public unsafe class EmoteTimelinePlayer
{
    private readonly IPluginLog log;

    public EmoteTimelinePlayer(IPluginLog log)
    {
        this.log = log;
    }

    /// <summary>
    /// Play a looped emote: blend the intro animation, then set BaseOverride for the loop.
    /// This is the pattern used by BypassEmote for persistent emotes like /playdead.
    /// </summary>
    public void PlayLoopedEmote(Character* native, ushort loopTimeline, ushort introTimeline, ulong targetObjId = 0)
    {
        if (native == null) return;

        try
        {
            // Blend intro animation first (the "falling down" part)
            if (introTimeline != 0)
                BlendTimeline(native, introTimeline, priority: 1, targetObjId: targetObjId);

            // Set base override to loop timeline (the "lying on ground" part)
            if (loopTimeline != 0)
                native->Timeline.BaseOverride = loopTimeline;

            log.Verbose($"EmoteTimelinePlayer: Played looped emote (intro={introTimeline}, loop={loopTimeline}, target=0x{targetObjId:X}).");
        }
        catch (Exception ex)
        {
            log.Error(ex, "EmoteTimelinePlayer: Failed to play looped emote.");
        }
    }

    /// <summary>
    /// Play a one-shot emote animation via blend.
    /// </summary>
    public void PlayOneShot(Character* native, ushort timelineId)
    {
        if (native == null || timelineId == 0) return;

        try
        {
            BlendTimeline(native, timelineId, priority: -1);
            log.Verbose($"EmoteTimelinePlayer: Played one-shot timeline {timelineId}.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "EmoteTimelinePlayer: Failed to play one-shot emote.");
        }
    }

    /// <summary>
    /// Reset emote state: clear BaseOverride and blend to idle.
    /// </summary>
    public void ResetEmote(Character* native)
    {
        if (native == null) return;

        try
        {
            native->Timeline.BaseOverride = 0;
            BlendTimeline(native, 3, priority: -1); // Timeline 3 = idle/reset blend
            log.Verbose("EmoteTimelinePlayer: Reset emote state.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "EmoteTimelinePlayer: Failed to reset emote.");
        }
    }

    /// <summary>
    /// Core blend: allocate ActionTimelineAnimParams, configure, and call PlayTimeline.
    /// Mirrors BypassEmote's ExperimentalBlend approach.
    /// </summary>
    private void BlendTimeline(Character* native, ushort actionTimeline, int priority = -1, ulong targetObjId = 0)
    {
        var animParams = (ActionTimelineAnimParams*)Marshal.AllocHGlobal(0x60);
        try
        {
            Unsafe.InitBlockUnaligned(animParams, 0, 0x60);
            animParams->Intensity = 1.0f;
            animParams->StartTS = 0.0f;
            animParams->Unk1C = -1.0f;
            animParams->Unk20 = 0;
            animParams->TargetObjId = targetObjId;
            animParams->Unk30 = 0;
            animParams->Priority = (uint)priority;
            animParams->Unk38 = -1;
            animParams->Unk3C = (actionTimeline == 3123) ? (byte)0 : (byte)0xFF;
            animParams->Unk42 = 0;

            native->Timeline.TimelineSequencer.PlayTimeline(actionTimeline, animParams);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)animParams);
        }
    }
}
