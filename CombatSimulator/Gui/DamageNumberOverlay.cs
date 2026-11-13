// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CombatSimulator.Simulation;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Gui;

/// <summary>
/// Damage numbers: every landed hit rises above the head of whoever took it and fades. White on
/// enemies, red on the player's side; a critical hit is larger and gold. A hit on the player, or by
/// the player, is much larger than the rest of the battle's, so the player's own fight reads at a
/// glance in a crowd. Drawn by the plugin itself, since the game's own flytext does not show for
/// client-side actors.
/// </summary>
public sealed unsafe class DamageNumberOverlay
{
    private const float LifeSeconds = 1.1f;
    private const float RiseMeters = 0.7f;
    private const int MaxNumbers = 160;
    // Text size against the font: the battle's hits, and the player's own (struck or striking).
    private const float BattleScale = 1.6f;
    private const float BattleCriticalScale = 2.1f;
    private const float OwnScale = 5.2f;
    private const float OwnCriticalScale = 6.6f;

    private static readonly Vector4 OnEnemy = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 OnFriend = new(1f, 0.35f, 0.3f, 1f);
    private static readonly Vector4 Critical = new(1f, 0.8f, 0.25f, 1f);
    // The player's own: what the player lands in orange-gold, what lands on the player in bright red
    // with a minus; both with a heavy outline, rising straight up without drifting.
    private static readonly Vector4 OwnDealt = new(1f, 0.62f, 0.1f, 1f);
    private static readonly Vector4 OwnDealtCritical = new(1f, 0.9f, 0.2f, 1f);
    private static readonly Vector4 OwnTaken = new(1f, 0.15f, 0.12f, 1f);
    // What lands on the player shows at the chest, apart from what the player lands on others.
    private const float ChestDrop = 0.75f;

    private readonly IGameGui gameGui;
    private readonly Configuration config;
    private readonly Func<SimulatedEntityState, nint> resolveAddress;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<Number> numbers = new();
    private readonly Random random = new();

    private sealed class Number
    {
        public nint Address;
        public Vector3 Anchor;      // where it started, used once the actor is gone
        public float Born;
        public float Drift;         // sideways spread, so a flurry of hits does not stack into one
        public string Text = string.Empty;
        public Vector4 Color;
        public float Scale;
        public bool Own;
        public float Lower;         // metres below the head it starts (the player's chest)
    }

    public DamageNumberOverlay(IGameGui gameGui, Configuration config, Func<SimulatedEntityState, nint> resolveAddress)
    {
        this.gameGui = gameGui;
        this.config = config;
        this.resolveAddress = resolveAddress;
    }

    /// <summary>A hit landed on this target (struck by <paramref name="source"/>, when known).</summary>
    public void Add(SimulatedEntityState target, DamageResult hit, SimulatedEntityState? source)
    {
        if (!config.ShowDamageNumbers || hit.Damage <= 0)
            return;

        var address = resolveAddress(target);
        if (address == nint.Zero || !TryHead(address, out var head))
            return;

        var friend = target.IsPlayer || target.IsCompanion;
        var taken = target.IsPlayer;
        var dealt = !taken && source?.IsPlayer == true;
        var own = taken || dealt;
        if (numbers.Count >= MaxNumbers)
            numbers.RemoveAt(0);
        numbers.Add(new Number
        {
            Address = address,
            Anchor = head,
            Born = (float)clock.Elapsed.TotalSeconds,
            Drift = own ? 0f : (float)(random.NextDouble() * 2 - 1),
            Text = (taken ? "-" : "") + (hit.IsDirectHit ? $"{hit.Damage:N0}!" : $"{hit.Damage:N0}"),
            Color = taken ? OwnTaken
                : dealt ? hit.IsCritical ? OwnDealtCritical : OwnDealt
                : hit.IsCritical ? Critical : friend ? OnFriend : OnEnemy,
            Own = own,
            Lower = taken ? ChestDrop : 0f,
            Scale = own
                ? hit.IsCritical ? OwnCriticalScale : OwnScale
                : hit.IsCritical ? BattleCriticalScale : BattleScale,
        });
    }

    public void Clear() => numbers.Clear();

    public void Draw()
    {
        if (numbers.Count == 0)
            return;

        var now = (float)clock.Elapsed.TotalSeconds;
        var drawList = ImGui.GetBackgroundDrawList();
        var font = ImGui.GetFont();
        var shadow = new Vector4(0f, 0f, 0f, 0.8f);

        for (var i = numbers.Count - 1; i >= 0; i--)
        {
            var n = numbers[i];
            var age = now - n.Born;
            if (age >= LifeSeconds)
            {
                numbers.RemoveAt(i);
                continue;
            }

            var t = age / LifeSeconds;
            // The actor may have moved (or fallen): follow it while it is there.
            var head = TryHead(n.Address, out var live) ? live : n.Anchor;
            var world = head + new Vector3(0f, RiseMeters * (1f - (1f - t) * (1f - t)) - n.Lower, 0f);
            if (!gameGui.WorldToScreen(world, out var screen))
                continue;

            // Pop in, hold, fade out.
            var pop = t < 0.12f ? 1f + 0.35f * (1f - t / 0.12f) : 1f;
            var alpha = t < 0.7f ? 1f : 1f - (t - 0.7f) / 0.3f;
            var size = ImGui.GetFontSize() * n.Scale * pop;
            var extent = ImGui.CalcTextSize(n.Text) * (n.Scale * pop);
            var pos = screen + new Vector2(n.Drift * 28f - extent.X / 2f, -extent.Y / 2f);

            var shadowColor = ImGui.ColorConvertFloat4ToU32(shadow with { W = shadow.W * alpha });
            if (n.Own)
            {
                // A heavy outline all round, so the player's own numbers read over anything.
                var thick = MathF.Max(2f, size * 0.06f);
                for (var k = 0; k < 8; k++)
                {
                    var a = k * MathF.PI / 4f;
                    drawList.AddText(font, size, pos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * thick, shadowColor, n.Text);
                }
            }
            else
                drawList.AddText(font, size, pos + new Vector2(2f, 2f), shadowColor, n.Text);
            drawList.AddText(font, size, pos, ImGui.ColorConvertFloat4ToU32(n.Color with { W = alpha }), n.Text);
        }
    }

    private static bool TryHead(nint address, out Vector3 head)
    {
        head = default;
        if (address == nint.Zero)
            return false;
        var obj = (GameObject*)address;
        if (obj->DrawObject == null)
            return false;
        head = new Vector3(obj->Position.X, obj->Position.Y + MathF.Max(1.2f, obj->Height) + 0.3f, obj->Position.Z);
        return true;
    }
}
