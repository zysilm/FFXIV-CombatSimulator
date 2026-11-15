// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CombatSimulator.Camera.Cutscene;

public enum CutsceneTrigger { Death, Victory, PlayNow }

/// <summary>Who is around when a shot is about to play (character addresses; zero = none).</summary>
public readonly record struct CutsceneRoleInput(
    CutsceneTrigger Trigger,
    bool PlayerAlive,
    nint Player,
    // Who killed the player (death).
    nint Killer,
    // The last enemy to fall (victory).
    nint LastFallenEnemy,
    // The ally who landed the final blow on the last enemy, zero when it was the player or is unknown.
    nint FinalBlowAlly,
    // The living ally nearest to the player's body.
    nint NearestLivingAlly,
    // Play Now: the current target, or the nearest enemy.
    nint PlayNowOther);

/// <summary>The resolved roles: the subject (the winner, or the body) and the other (the killer, or the last enemy to fall).</summary>
public readonly record struct CutsceneRoleResult(nint Subject, nint Other, string Rule);

/// <summary>
/// The user's role rules (docs/CUTSCENE_SHOTS.md §6):
/// single shots — death: the player; victory: the player if alive, else the deciding ally;
/// two-person — death: subject = the player (the body), other = the killer; victory, player alive: subject = the player,
/// other = the last enemy to fall; victory, player dead: subject = the ally who landed the final blow on the last enemy
/// (the nearest living ally if that is unknown), other = the last enemy to fall. Play Now: subject = the player, other =
/// the current target or the nearest enemy. A required actor that is missing skips the shot (with the reason).
/// Game-free.
/// </summary>
public static class CutsceneRoles
{
    public static CutsceneRoleResult? Resolve(bool twoPerson, in CutsceneRoleInput i, out string why)
    {
        why = "";
        nint subject;
        nint other = 0;
        string rule;
        switch (i.Trigger)
        {
            case CutsceneTrigger.Death:
                subject = i.Player;
                rule = "death: subject = player";
                if (twoPerson) { other = i.Killer; rule += ", other = killer"; }
                break;
            case CutsceneTrigger.Victory:
                if (i.PlayerAlive)
                {
                    subject = i.Player;
                    rule = "victory: subject = player";
                }
                else if (i.FinalBlowAlly != 0)
                {
                    subject = i.FinalBlowAlly;
                    rule = "victory (player down): subject = ally with the final blow";
                }
                else
                {
                    subject = i.NearestLivingAlly;
                    rule = "victory (player down): subject = nearest living ally (final blow unknown)";
                }
                if (twoPerson) { other = i.LastFallenEnemy; rule += ", other = last enemy to fall"; }
                break;
            default:
                subject = i.Player;
                rule = "play now: subject = player";
                if (twoPerson) { other = i.PlayNowOther; rule += ", other = target or nearest enemy"; }
                break;
        }
        if (subject == 0) { why = $"no subject ({rule})"; return null; }
        if (twoPerson && other == 0) { why = $"no other actor for a two-person shot ({rule})"; return null; }
        if (twoPerson && other == subject) { why = $"subject and other are the same actor ({rule})"; return null; }
        return new CutsceneRoleResult(subject, other, rule);
    }

    /// <summary>Which of our actors stands in for a cutscene actor of the entry: the followed one (camera root) takes
    /// the entry's <c>follow</c> role, the looked-at one its <c>lookAt</c> role.</summary>
    public static nint ActorFor(CutsceneShotEntry entry, int cutsceneActor, in CutsceneRoleResult roles)
    {
        string role;
        if (cutsceneActor == entry.FollowActor) role = entry.Follow;
        else if (entry.LookActor == cutsceneActor) role = entry.LookAt ?? "other";
        else role = entry.Follow; // not expected in the curated list (eye on a third actor is filtered out)
        return role == "other" ? roles.Other : roles.Subject;
    }
}
