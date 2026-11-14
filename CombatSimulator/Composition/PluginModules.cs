// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Dalamud.Plugin.Services;

namespace CombatSimulator.Composition;

/// <summary>
/// The plugin's feature modules (docs/PLUGIN_ARCHITECTURE.md §6.1). Creating them builds nothing: every
/// module starts empty and is filled by its build steps, which the plugin's constructor calls in the one
/// order the game objects and hooks have always been created in. A module reads another module's
/// objects when it uses them (a late lookup, as the plugin's fields were read), so a module may be
/// referred to before its own objects exist.
/// </summary>
internal sealed class PluginModules
{
    public Configuration Config { get; }
    public IPluginLog Log { get; }

    public WorldModule World { get; }
    public ActorsModule Actors { get; }
    public CorpseModule Corpse { get; }
    public CameraModule Camera { get; }
    public CombatModule Combat { get; }
    public FightingModule Fighting { get; }
    public BattleModule Battle { get; }
    public PresentationModule Presentation { get; }
    public SafetyDevModule SafetyDev { get; }

    public PluginModules(Configuration config, IPluginLog log)
    {
        Config = config;
        Log = log;
        World = new WorldModule();
        Actors = new ActorsModule(this);
        Corpse = new CorpseModule(this);
        Camera = new CameraModule(this);
        Combat = new CombatModule(this);
        Fighting = new FightingModule(this);
        Battle = new BattleModule(this);
        Presentation = new PresentationModule(this);
        SafetyDev = new SafetyDevModule(this);
    }
}
