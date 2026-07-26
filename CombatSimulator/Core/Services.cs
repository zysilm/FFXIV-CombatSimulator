// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Core;

public static class Services
{
    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    public static IClientState ClientState { get; private set; } = null!;
    public static IObjectTable ObjectTable { get; private set; } = null!;
    public static IFramework Framework { get; private set; } = null!;
    public static IGameInteropProvider GameInterop { get; private set; } = null!;
    public static IDataManager DataManager { get; private set; } = null!;
    public static IGameGui GameGui { get; private set; } = null!;
    public static IChatGui ChatGui { get; private set; } = null!;
    public static ICondition Condition { get; private set; } = null!;
    public static IPluginLog Log { get; private set; } = null!;

    public static void Init(
        IDalamudPluginInterface pluginInterface,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        IGameInteropProvider gameInterop,
        IDataManager dataManager,
        IGameGui gameGui,
        IChatGui chatGui,
        ICondition condition,
        IPluginLog log)
    {
        PluginInterface = pluginInterface;
        ClientState = clientState;
        ObjectTable = objectTable;
        Framework = framework;
        GameInterop = gameInterop;
        DataManager = dataManager;
        GameGui = gameGui;
        ChatGui = chatGui;
        Condition = condition;
        Log = log;
    }
}
