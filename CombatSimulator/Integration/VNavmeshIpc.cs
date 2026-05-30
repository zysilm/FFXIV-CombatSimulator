using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Integration;

public class VNavmeshIpc
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private DateTime lastStatusCheck = DateTime.MinValue;
    private bool availabilityKnown;

    public bool IsAvailable { get; private set; }
    public bool IsReady { get; private set; }
    public float BuildProgress { get; private set; } = -1f;
    public string StatusText { get; private set; } = "vnavmesh status has not been checked.";

    public bool CanPathfind => IsAvailable && IsReady;

    public VNavmeshIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public void RefreshStatus(bool force = false)
    {
        if (!force && availabilityKnown && DateTime.UtcNow - lastStatusCheck < TimeSpan.FromSeconds(1))
            return;

        lastStatusCheck = DateTime.UtcNow;
        availabilityKnown = true;

        try
        {
            var isReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
            var buildProgress = pluginInterface.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress");

            IsReady = isReady.InvokeFunc();
            BuildProgress = buildProgress.InvokeFunc();
            IsAvailable = true;

            if (IsReady)
                StatusText = "vnavmesh is ready.";
            else if (BuildProgress >= 0)
                StatusText = $"vnavmesh is building navigation data ({BuildProgress * 100f:F0}%).";
            else
                StatusText = "vnavmesh is loaded, but navigation data is not ready.";
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            IsReady = false;
            BuildProgress = -1f;
            StatusText = "vnavmesh is not installed or not loaded.";
            log.Verbose($"VNavmeshIpc: unavailable ({ex.Message})");
        }
    }

    public Task<List<Vector3>> Pathfind(Vector3 from, Vector3 to, float tolerance)
    {
        var pathfind = pluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, float, Task<List<Vector3>>>(
            "vnavmesh.Nav.PathfindWithTolerance");
        return pathfind.InvokeFunc(from, to, false, tolerance);
    }

    public Vector3? NearestPointReachable(Vector3 point, float halfExtentXz = 5f, float halfExtentY = 10f)
    {
        var nearest = pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>(
            "vnavmesh.Query.Mesh.NearestPointReachable");
        return nearest.InvokeFunc(point, halfExtentXz, halfExtentY);
    }

    public Vector3? PointOnFloor(Vector3 point, bool allowUnlandable = false, float halfExtentXz = 5f)
    {
        var floor = pluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>(
            "vnavmesh.Query.Mesh.PointOnFloor");
        return floor.InvokeFunc(point, allowUnlandable, halfExtentXz);
    }
}
