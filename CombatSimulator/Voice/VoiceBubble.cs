// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace CombatSimulator.Voice;

/// <summary>
/// The client's own overhead speech balloon on an enemy, opened locally. Same call the crowd chatter
/// uses; printToLog stays false so nothing reaches the chat log, and nothing is sent anywhere.
/// The balloon closes itself after its duration.
/// </summary>
public static unsafe class VoiceBubble
{
    // Head-height attachment used by the client's own yell balloons (same as crowd chatter).
    private const byte BalloonParentBone = 25;
    private static bool failed;

    /// <summary>Caller guarantees <paramref name="address"/> is a live Character this frame.</summary>
    public static bool Open(nint address, string text, float seconds, IPluginLog log)
    {
        if (failed || address == nint.Zero || string.IsNullOrWhiteSpace(text))
            return false;
        try
        {
            var character = (Character*)address;
            if (character->YellBalloon.State != NpcYellBalloonState.Inactive)
                character->YellBalloon.CloseBalloon();
            character->YellBalloon.OpenBalloon(text, Math.Clamp(seconds, 1f, 10f), true, 0f, false, false, true, BalloonParentBone);
            return true;
        }
        catch (Exception ex)
        {
            // One failure turns bubbles off for the session; voices keep playing.
            failed = true;
            log.Warning($"Enemy voices: speech bubble failed ({ex.GetType().Name}: {ex.Message}); bubbles off for this session.");
            return false;
        }
    }
}
