// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using FFXIVClientStructs.FFXIV.Client.Sound;

namespace CombatSimulator.Animation;

public unsafe partial class AnimationController
{
    private sealed record WeaponSoundBank(string Path, byte[] Data);
    private readonly Dictionary<uint, WeaponSoundBank?> weaponSoundBanks = new();
    private WeaponSoundBank? observedWeaponSoundBank;
    private long nativeWeaponSoundTime;
    private bool playingPlayerImpact;
    private const uint WeaponImpactSoundIndex = 1;
    private const int MaxPendingPlayerImpacts = 64;
    private record struct PendingPlayerImpact(uint Target, float Timer, long Started, WeaponSoundBank Bank);
    private readonly List<PendingPlayerImpact> pendingPlayerImpactSounds = new();

    // Resolve actual game resources once per weapon family; never load files in the audio hook.
    private WeaponSoundBank? ResolvePlayerWeaponSoundBank()
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) return null;
        var job = player.ClassJob.RowId;
        if (weaponSoundBanks.TryGetValue(job, out var cached)) return cached;
        string[] families = job switch
        {
            1 or 19 => new[] { "sword" },
            2 or 20 => new[] { "knuckle" },
            3 or 21 => new[] { "axe", "great_axe", "greataxe" },
            4 or 22 => new[] { "spear", "lance" },
            5 or 23 => new[] { "bow" },
            6 or 24 => new[] { "staff", "wand" },
            7 or 25 or 36 or 42 => new[] { "rod", "staff" },
            26 or 27 or 28 => new[] { "book" },
            29 or 30 => new[] { "dagger" },
            31 => new[] { "gun", "musket" },
            32 => new[] { "greatsword", "twohandsword", "sword" },
            33 => new[] { "globe", "card" },
            34 => new[] { "katana", "sword" },
            35 => new[] { "rapier", "sword" },
            37 => new[] { "gunblade", "sword" },
            38 => new[] { "chakram", "dagger" },
            39 => new[] { "scythe", "sickle" },
            40 => new[] { "noulith" },
            41 => new[] { "dualsword", "sword" },
            _ => Array.Empty<string>(),
        };
        WeaponSoundBank? bank = null;
        foreach (var family in families)
        {
            var path = $"sound/battle/wep/{family}_30.scd";
            var data = dataManager.GetFile(path)?.Data;
            // The SCD table at HeaderSize starts with the number of playable sub-sounds.
            if (data == null || data.Length < 96 || !data.AsSpan(0, 8).SequenceEqual("SEDBSSCF"u8)) continue;
            var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14, 2));
            if (headerSize > data.Length - 2 ||
                BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(headerSize, 2)) <= WeaponImpactSoundIndex) continue;
            bank = new WeaponSoundBank(path, data);
            break;
        }
        weaponSoundBanks[job] = bank;
        if (bank != null) log.Info($"Player weapon impact bank: {bank.Path}, sound={WeaponImpactSoundIndex}.");
        return bank;
    }

    public void QueuePlayerWeaponImpact(uint targetEntityId, float delay)
    {
        var bank = ResolvePlayerWeaponSoundBank();
        if (bank == null || pendingPlayerImpactSounds.Count >= MaxPendingPlayerImpacts) return;
        Volatile.Write(ref observedWeaponSoundBank, bank);
        pendingPlayerImpactSounds.Add(new PendingPlayerImpact(targetEntityId,
            Math.Clamp(delay, 0f, 1f), Environment.TickCount64, bank));
    }

    // Reuse the existing sound hook solely to observe native playback. No sound is redirected here.
    private void ObservePlayerWeaponSound(long info, int index)
    {
        var bank = Volatile.Read(ref observedWeaponSoundBank);
        if (bank == null || info == 0 || playingPlayerImpact || index != WeaponImpactSoundIndex) return;
        var data = *(byte**)(info + 8);
        if (data != null && new ReadOnlySpan<byte>(data, 96).SequenceEqual(bank.Data.AsSpan(0, 96)))
            Interlocked.Exchange(ref nativeWeaponSoundTime, Environment.TickCount64);
    }

    private void TickPlayerImpactSounds(float dt)
    {
        for (var i = pendingPlayerImpactSounds.Count - 1; i >= 0; i--)
        {
            var hit = pendingPlayerImpactSounds[i];
            hit.Timer -= dt;
            if (hit.Timer > 0f) { pendingPlayerImpactSounds[i] = hit; continue; }
            pendingPlayerImpactSounds.RemoveAt(i);
            if (Core.Services.ObjectTable.LocalPlayer == null) continue;
            // Native playback during the wind-up already supplies this attack's weapon cue.
            if (Interlocked.Read(ref nativeWeaponSoundTime) >= hit.Started) continue;
            var (target, _) = ResolveActorAddress(hit.Target, false);
            var manager = SoundManager.Instance();
            if (target == 0 || manager == null) continue;
            var position = ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target)->Position;
            try
            {
                playingPlayerImpact = true;
                // Use the complete native playback path, including finalization and auto-release.
                manager->PlaySound(hit.Bank.Path, 1f, 0, position.X, position.Y, position.Z, 1f, 0,
                    WeaponImpactSoundIndex, true, SoundVolumeCategory.Player, false, -1, false, false, true, false);
            }
            catch (Exception ex) { log.Warning(ex, "Player weapon impact playback failed."); }
            finally { playingPlayerImpact = false; }
        }
    }
}
