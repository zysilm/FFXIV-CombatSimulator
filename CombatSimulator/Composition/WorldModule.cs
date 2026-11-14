// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Animation;
using CombatSimulator.Integration;
using CombatSimulator.Safety;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Composition;

/// <summary>
/// Reading and writing the game: animation, bones, the movement block, the other plugins' IPC, the
/// rendered world geometry, and the navigation mesh with its crowd (docs/PLUGIN_ARCHITECTURE.md §6.1).
/// Built in steps that the plugin's constructor calls where these objects were always built (several
/// hook or subscribe to the game when created, so the order between modules is kept as it was).
/// </summary>
internal sealed class WorldModule
{
    private GlamourerIpc glamourerIpc = null!;
    private VNavmeshIpc vnavmeshIpc = null!;
    private MovementBlockHook movementBlockHook = null!;
    private AnimationController animationController = null!;
    private BoneTransformService boneTransformService = null!;
    private Rendering.WorldGeometry.WorldGeometryRenderer worldGeometryRenderer = null!;
    private Rendering.WorldGeometry.WorldGeometryPreview worldGeometryPreview = null!;
    private Navigation.NavMeshService navMesh = null!;
    private Navigation.CrowdMover crowdMover = null!;

    public GlamourerIpc Glamourer => glamourerIpc;
    public VNavmeshIpc VNavmesh => vnavmeshIpc;
    public MovementBlockHook MovementBlock => movementBlockHook;
    public AnimationController Animation => animationController;
    public BoneTransformService BoneTransforms => boneTransformService;
    public Rendering.WorldGeometry.WorldGeometryRenderer GeometryRenderer => worldGeometryRenderer;
    public Rendering.WorldGeometry.WorldGeometryPreview GeometryPreview => worldGeometryPreview;
    public Navigation.NavMeshService NavMesh => navMesh;
    public Navigation.CrowdMover Crowd => crowdMover;

    /// <summary>The other plugins' IPC, the movement block hook and animation.</summary>
    public void CreateGameAccess(IDalamudPluginInterface pluginInterface, IGameInteropProvider gameInterop,
        IClientState clientState, IDataManager dataManager, ISigScanner sigScanner, Configuration config, IPluginLog log)
    {
        glamourerIpc = new GlamourerIpc(pluginInterface, log);
        vnavmeshIpc = new VNavmeshIpc(pluginInterface, log);
        movementBlockHook = new MovementBlockHook(gameInterop, clientState, log);
        animationController = new AnimationController(log, clientState, dataManager, gameInterop, sigScanner, config);
    }

    public void CreateBoneTransforms(IGameInteropProvider gameInterop, ISigScanner sigScanner, IPluginLog log)
    {
        boneTransformService = new BoneTransformService(gameInterop, sigScanner, log);
    }

    public void CreateGeometryRenderer(IGameInteropProvider gameInterop, ISigScanner sigScanner, IPluginLog log)
    {
        worldGeometryRenderer = new Rendering.WorldGeometry.WorldGeometryRenderer(gameInterop, sigScanner, log);
    }

    /// <summary>The geometry preview, the navigation mesh and its crowd.</summary>
    public void CreateNavigation(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        worldGeometryPreview = new Rendering.WorldGeometry.WorldGeometryPreview(worldGeometryRenderer);
        navMesh = new Navigation.NavMeshService(worldGeometryRenderer, log);
        crowdMover = new Navigation.CrowdMover(navMesh, log);
        navMesh.DumpPath = System.IO.Path.Combine(pluginInterface.GetPluginConfigDirectory(), "navmesh-last-input.bin");
    }
}
