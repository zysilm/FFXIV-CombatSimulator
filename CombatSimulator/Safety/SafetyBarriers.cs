// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Targeting;

namespace CombatSimulator.Safety;

/// <summary>
/// The client-side safety barriers, in one place: the hooks that keep simulated combat from ever reaching the
/// server. Every path that starts a simulation or spawns simulated actors asks <see cref="AllHealthy"/> here
/// (the main window's buttons, the slash command, the spawners), and the Diagnose tab's "Client-Side Safety"
/// panel shows each barrier.
/// </summary>
public sealed class SafetyBarriers
{
    private readonly UseActionHook useAction;
    private readonly PlayerTargetController targets;

    public SafetyBarriers(UseActionHook useAction, PlayerTargetController targets)
    {
        this.useAction = useAction;
        this.targets = targets;
    }

    /// <summary>Hotbar / keybind actions (<c>ActionManager.UseAction</c>).</summary>
    public bool UseActionHealthy => useAction.IsUseActionHealthy;

    /// <summary>Queued and direct actions (<c>ActionManager.UseActionLocation</c>).</summary>
    public bool UseActionLocationHealthy => useAction.IsUseActionLocationHealthy;

    /// <summary>Clicking a simulated actor (<c>TargetSystem.SetHardTarget</c>).</summary>
    public bool HardTargetGuardHealthy => targets.IsHardTargetGuardHealthy;

    /// <summary>True when every barrier is installed.</summary>
    public bool AllHealthy => UseActionHealthy && UseActionLocationHealthy && HardTargetGuardHealthy;

    /// <summary>The first barrier that is down, for a chat or log line; null when all are up.</summary>
    public string? FirstDown =>
        !UseActionHealthy ? "UseAction interceptor"
        : !UseActionLocationHealthy ? "UseActionLocation interceptor"
        : !HardTargetGuardHealthy ? "Hard-target guard"
        : null;
}
