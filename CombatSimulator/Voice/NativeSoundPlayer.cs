// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;

namespace CombatSimulator.Voice;

/// <summary>
/// Local-only playback of a game .scd through the client's own SoundManager.PlaySound — the same
/// call the client uses for action and footstep sounds, so nothing leaves the machine.
///
/// Nothing is resolved by this class at load: the address comes from ClientStructs' resolver,
/// which Dalamud runs for every plugin anyway, and is only checked (never called) until the first
/// play. A missing address, a missing instance, or a managed exception disables playback for the
/// session with a single log line, so a game patch that breaks the signature costs silence, not a
/// crash loop.
/// </summary>
public sealed unsafe class NativeSoundPlayer
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private bool checkedOnce;
    private bool available;

    public NativeSoundPlayer(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>Why playback is off, for the settings panel. Null while usable or unchecked.</summary>
    public string? FailureReason { get; private set; }
    public int PlayCount { get; private set; }

    public bool IsAvailable
    {
        get
        {
            if (!checkedOnce)
                Check();
            return available;
        }
    }

    private void Check()
    {
        checkedOnce = true;
        try
        {
            if ((nint)SoundManager.MemberFunctionPointers.PlaySound == 0)
            {
                Disable("PlaySound address did not resolve (game patch?)");
                return;
            }
            if (SoundManager.StaticAddressPointers.ppInstance == null)
            {
                Disable("SoundManager instance pointer did not resolve");
                return;
            }
            available = true;
        }
        catch (Exception ex)
        {
            Disable($"resolving PlaySound threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Disable(string reason)
    {
        available = false;
        if (FailureReason != null)
            return;
        FailureReason = reason;
        log.Warning($"Enemy voices disabled for this session: {reason}.");
    }

    /// <summary>
    /// Play one sound entry of an existing game file. Returns the SoundData handle (0 on refusal).
    /// The handle is only good for <see cref="TryStop"/>: auto-released entries are recycled.
    /// </summary>
    public nint Play(string path, uint soundNumber, Vector3? position, float volume, int volumeCategory)
    {
        if (string.IsNullOrEmpty(path) || !IsAvailable)
            return 0;
        // A path the client cannot find would make the engine log/allocate for nothing at best;
        // every caller resolves through FileExists already, this is the last line.
        if (!dataManager.FileExists(path))
            return 0;
        try
        {
            var manager = *SoundManager.StaticAddressPointers.ppInstance;
            if (manager == null)
                return 0; // Sound system not up yet (or torn down): skip, don't disable.
            var pos = position ?? Vector3.Zero;
            var category = Enum.IsDefined(typeof(SoundVolumeCategory), volumeCategory) && volumeCategory != (int)SoundVolumeCategory.NoPlay
                ? (SoundVolumeCategory)volumeCategory
                : SoundVolumeCategory.Other;
            // Arguments are ClientStructs' documented defaults except path/volume/position/entry/
            // category. autoRelease=true: there are only 256 SoundData entries and nothing here
            // needs to keep one.
            var data = manager->PlaySound(path, Math.Clamp(volume, 0f, 1f), 0, pos.X, pos.Y, pos.Z, 1f, 0,
                soundNumber, true, category, false, -1, false, false, position.HasValue, false);
            PlayCount++;
            return (nint)data;
        }
        catch (Exception ex)
        {
            Disable($"PlaySound threw {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Stop a sound this player started, if that SoundData entry is still playing the same file and
    /// entry (it may have been released and handed to another sound since).
    /// </summary>
    public void TryStop(nint handle, string path, uint soundNumber)
    {
        if (handle == 0 || !available)
            return;
        try
        {
            // SoundData entries live in the manager's fixed pool, so the pointer stays readable even
            // after release; the checks below make sure it is still *our* sound before stopping it.
            var data = (SoundData*)handle;
            if (data->SoundNumber != soundNumber || !data->IsPlaying())
                return;
            var name = data->GetFileName().ToString();
            if (!string.Equals(name, path, StringComparison.OrdinalIgnoreCase))
                return;
            data->Stop(200);
        }
        catch (Exception ex)
        {
            log.Warning($"Enemy voices: stop failed ({ex.GetType().Name}); ignoring.");
        }
    }
}
