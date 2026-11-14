// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Crowd;

namespace CombatSimulator.Navigation;

/// <summary>
/// Moves actors with a DetourCrowd on the plugin's navigation mesh: each one follows a corridor of
/// polygons to its goal, steers round the others, slows to a stop on arrival, and stands on the floor
/// of the polygon it is on (never a roof over it). The caller decides goals each frame
/// (<see cref="Drive"/>), steps the crowd once (<see cref="Update"/>), then writes the positions back
/// to the game (<see cref="TryGetMotion"/>). Framework thread only.
/// </summary>
public sealed class CrowdMover : Combat.ICrowdAgents
{
    // A goal that has moved less than this is not sent again: re-planning every frame for a target
    // that shuffles would keep the agent turning.
    private const float RegoalDistance = 1.0f;
    // How far above or below its own height an actor looks for the floor it stands on.
    private const float FloorSearch = 3f;
    // An actor beside the mesh (it keeps clear of walls and drops ledge edges) steps onto it from this far.
    private const float JoinSideways = 4f;
    // The mesh's surface can sit a little above the real ground; the exact ground is looked for this
    // far above and below it.
    private const float GroundAbove = 0.6f;
    private const float GroundBelow = 1.2f;
    private const float ReportSeconds = 5f;
    // Someone just spawned (not driven in the last second) beside the ground the fight is on (off a
    // bridge, on a ledge, in water) is put on that ground from this far, instead of on whatever island
    // it landed on, where it would be stuck for the whole fight.
    private const float SpawnSnapSideways = 20f;
    private const float SpawnSnapVertical = 12f;
    private const int FreshAfterFrames = 60;
    private const float MaxAgentRadius = 1.5f;
    private const int AgentsWarmUp = 24;

    private readonly NavMeshService navMesh;
    private readonly IPluginLog log;
    private DtCrowd? crowd;
    private DtNavMesh? crowdMesh;
    private int frame;
    private readonly List<nint> stale = new();

    private sealed class Agent
    {
        public DtCrowdAgent Crowd = null!;
        public Vector3 Goal;
        public bool HasGoal;
        public int Frame;
        public float Radius;
        public float Speed;
    }

    private readonly Dictionary<nint, Agent> agents = new();
    // The frame each actor was last driven: a rebuilt mesh re-adds everyone, but only someone not
    // seen for a while is new to the fight.
    private readonly Dictionary<nint, int> lastDriven = new();
    private int spawnSnaps;

    public CrowdMover(NavMeshService navMesh, IPluginLog log)
    {
        this.navMesh = navMesh;
        this.log = log;
    }

    /// <summary>Whether there is a mesh to move on (built around the player, in this zone).</summary>
    public bool Ready
    {
        get
        {
            Sync();
            return crowd != null;
        }
    }

    /// <summary>
    /// Ask this actor to walk to <paramref name="goal"/> (or stand where it is, when null) this frame.
    /// The actor joins the crowd at <paramref name="position"/>, snapped to the floor under it, the
    /// first time. False when it is not over the mesh (it then stands).
    /// </summary>
    public bool Drive(nint address, Vector3 position, float radius, float speed, Vector3? goal)
    {
        Sync();
        if (crowd == null || address == nint.Zero)
            return false;

        radius = Math.Clamp(radius, 0.3f, MaxAgentRadius);
        var fresh = !lastDriven.TryGetValue(address, out var seen) || frame - seen > FreshAfterFrames;
        lastDriven[address] = frame;
        if (!agents.TryGetValue(address, out var agent))
        {
            // New to the fight and not on the ground the player can reach: onto that ground.
            var onFight = navMesh.TryFloor(position, FloorSearch, out var floor, out var floorRef) && navMesh.OnPlayersPiece(floorRef);
            if (fresh && !onFight && navMesh.TryFloorOnPlayersPiece(position, SpawnSnapVertical, SpawnSnapSideways, out var snapped))
            {
                floor = snapped;
                spawnSnaps++;
            }
            // Standing on a piece of mesh that was switched off (not connected to where the player
            // stood): it is real ground all the same, so switch it on.
            else if (!onFight && !navMesh.TryFloor(position, FloorSearch, out floor, out _) &&
                (!navMesh.EnablePieceUnder(position, FloorSearch) || !navMesh.TryFloor(position, FloorSearch, out floor, out _)) &&
                !navMesh.TryFloor(position, FloorSearch, JoinSideways, out floor, out _) &&
                (!navMesh.EnablePieceUnder(position, FloorSearch, JoinSideways) || !navMesh.TryFloor(position, FloorSearch, JoinSideways, out floor, out _)))
            {
                NoteRefused(position);
                return false;
            }
            var p = AgentParams(radius, speed);
            var crowdAgent = crowd.AddAgent(NavMeshBuild.ToRc(floor), p);
            if (crowdAgent == null)
                return false;
            agent = new Agent { Crowd = crowdAgent, Radius = radius, Speed = speed };
            agents[address] = agent;
        }
        else if (MathF.Abs(agent.Speed - speed) > 0.05f || MathF.Abs(agent.Radius - radius) > 0.05f)
        {
            agent.Speed = speed;
            agent.Radius = radius;
            crowd.UpdateAgentParameters(agent.Crowd, AgentParams(radius, speed));
        }
        agent.Frame = frame;

        if (goal is not { } wanted)
        {
            if (agent.HasGoal)
            {
                crowd.ResetMoveTarget(agent.Crowd);
                agent.HasGoal = false;
            }
            return true;
        }

        var stillValid = agent.Crowd.targetState is DtMoveRequestState.DT_CROWDAGENT_TARGET_VALID
            or DtMoveRequestState.DT_CROWDAGENT_TARGET_REQUESTING
            or DtMoveRequestState.DT_CROWDAGENT_TARGET_WAITING_FOR_QUEUE
            or DtMoveRequestState.DT_CROWDAGENT_TARGET_WAITING_FOR_PATH;
        if (agent.HasGoal && stillValid && Vector3.Distance(agent.Goal, wanted) < RegoalDistance)
            return true;

        // The goal on the floor nearest it; a goal off the mesh (the target in the air, on a ledge)
        // is taken at the nearest walkable point.
        if (!navMesh.TryFloor(wanted, FloorSearch, out var target, out var targetRef) &&
            !navMesh.TryFloor(wanted, FloorSearch * 4f, out target, out targetRef))
            return true;
        crowd.RequestMoveTarget(agent.Crowd, targetRef, NavMeshBuild.ToRc(target));
        agent.Goal = wanted;
        agent.HasGoal = true;
        return true;
    }

    /// <summary>The walkable floor at or beside a point (a few metres sideways), for a spot to walk to.</summary>
    public bool TrySnapToFloor(Vector3 point, out Vector3 floor)
    {
        floor = point;
        return navMesh.Mesh != null && navMesh.TryFloor(point, FloorSearch, JoinSideways, out floor, out _);
    }

    /// <summary>
    /// Something the crowd walks round but does not move (the player): put where it is now, standing.
    /// Call every frame, like <see cref="Drive"/>.
    /// </summary>
    public void SetObstacle(nint address, Vector3 position, float radius)
    {
        Sync();
        if (crowd == null || address == nint.Zero)
            return;
        Remove(address);
        if (!navMesh.TryFloor(position, FloorSearch, out var floor, out _))
            return;
        var p = AgentParams(Math.Clamp(radius, 0.3f, MaxAgentRadius), 0.5f);
        p.updateFlags = 0;
        var crowdAgent = crowd.AddAgent(NavMeshBuild.ToRc(floor), p);
        if (crowdAgent != null)
            agents[address] = new Agent { Crowd = crowdAgent, Radius = radius, Speed = 0f, Frame = frame };
    }

    /// <summary>Step the crowd once; actors not driven since the last step leave it.</summary>
    public void Update(float deltaTime)
    {
        if (crowd == null)
            return;

        stale.Clear();
        foreach (var (address, agent) in agents)
            if (agent.Frame != frame)
                stale.Add(address);
        foreach (var address in stale)
            Remove(address);
        if (frame % 600 == 0)
        {
            stale.Clear();
            foreach (var (address, seen) in lastDriven)
                if (frame - seen > 600)
                    stale.Add(address);
            foreach (var address in stale)
                lastDriven.Remove(address);
        }

        if (agents.Count > 0 && deltaTime > 0f)
            crowd.Update(MathF.Min(deltaTime, 0.1f), null);
        frame++;
        Report();
    }

    /// <summary>
    /// Where the crowd has put this actor, and how fast it is going. The height is the real ground there
    /// (a short ray just around the mesh's surface), so feet stand on it rather than on the voxels.
    /// </summary>
    public bool TryGetMotion(nint address, out Vector3 position, out Vector3 velocity)
    {
        position = velocity = default;
        if (!agents.TryGetValue(address, out var agent))
            return false;
        position = NavMeshBuild.ToVector(agent.Crowd.npos);
        velocity = NavMeshBuild.ToVector(agent.Crowd.vel);
        if (!float.IsFinite(position.X + position.Y + position.Z))
            return false;
        if (Simulation.TerrainHeightService.TrySampleGroundNear(position, GroundAbove, GroundBelow, out var ground))
            position.Y = ground;
        return true;
    }

    // What the crowd did, written to the log every few seconds while it moves anyone.
    private int refusedCount;
    private Vector3 lastRefused;
    private readonly System.Diagnostics.Stopwatch reportClock = System.Diagnostics.Stopwatch.StartNew();

    private void NoteRefused(Vector3 position)
    {
        refusedCount++;
        lastRefused = position;
    }

    private void Report()
    {
        if (reportClock.Elapsed.TotalSeconds < ReportSeconds)
            return;
        reportClock.Restart();
        if (spawnSnaps > 0)
        {
            log.Info($"Crowd: {spawnSnaps} new actors were off the ground the fight is on and were put onto it.");
            spawnSnaps = 0;
        }
        if (agents.Count == 0 && refusedCount == 0)
            return;
        log.Info(refusedCount == 0
            ? $"Crowd: {agents.Count} actors on the mesh, none refused."
            : $"Crowd: {agents.Count} actors on the mesh; {refusedCount} refusals in {ReportSeconds:0} s (no mesh within {JoinSideways:0} m), the last at {lastRefused}.");
        refusedCount = 0;
    }

    public void Remove(nint address)
    {
        if (agents.Remove(address, out var agent))
            crowd?.RemoveAgent(agent.Crowd);
    }

    public void Clear()
    {
        if (crowd != null)
            foreach (var agent in agents.Values)
                crowd.RemoveAgent(agent.Crowd);
        agents.Clear();
        lastDriven.Clear();
    }

    // A new mesh (built, rebuilt around the player, or cleared): a new crowd on it. Actors rejoin at
    // their current positions the next time they are driven.
    private void Sync()
    {
        var mesh = navMesh.Mesh;
        if (ReferenceEquals(mesh, crowdMesh))
            return;
        agents.Clear();
        crowdMesh = mesh;
        crowd = mesh == null ? null : new DtCrowd(new DtCrowdConfig(MaxAgentRadius), mesh);
        if (mesh != null)
            WarmUp(mesh);
    }

    private DtCrowdAgentParams AgentParams(float radius, float speed)
    {
        return new DtCrowdAgentParams
        {
            radius = radius,
            height = 2f,
            maxAcceleration = MathF.Max(8f, speed * 4f),
            maxSpeed = MathF.Max(0.5f, speed),
            collisionQueryRange = MathF.Max(3f, radius * 8f),
            pathOptimizationRange = MathF.Max(6f, radius * 30f),
            separationWeight = 1.5f,
            updateFlags = DtCrowdAgentUpdateFlags.DT_CROWD_ANTICIPATE_TURNS | DtCrowdAgentUpdateFlags.DT_CROWD_OBSTACLE_AVOIDANCE |
                          DtCrowdAgentUpdateFlags.DT_CROWD_SEPARATION | DtCrowdAgentUpdateFlags.DT_CROWD_OPTIMIZE_VIS |
                          DtCrowdAgentUpdateFlags.DT_CROWD_OPTIMIZE_TOPO,
            obstacleAvoidanceType = 2,
        };
    }

    // .NET re-compiles hot code during its first seconds of use, which made the first crowd updates
    // take up to 10 ms (Tests/Navigation). Play a throwaway crowd on the new mesh off the framework
    // thread first, so that happens before a fight. The mesh is only read.
    private void WarmUp(DtNavMesh mesh)
    {
        var p = AgentParams(0.45f, 6f);
        Task.Run(() =>
        {
            try
            {
                var warm = new DtCrowd(new DtCrowdConfig(MaxAgentRadius), mesh);
                var query = new DtNavMeshQuery(mesh);
                var filter = new DtQueryDefaultFilter();
                var random = new DotRecast.Core.RcRand(7);
                var added = new List<DtCrowdAgent>();
                for (var i = 0; i < AgentsWarmUp; i++)
                {
                    if (query.FindRandomPoint(filter, random, out _, out var start).Failed()) continue;
                    if (query.FindRandomPoint(filter, random, out var goalRef, out var goal).Failed()) continue;
                    var a = warm.AddAgent(start, p);
                    if (a == null) continue;
                    warm.RequestMoveTarget(a, goalRef, goal);
                    added.Add(a);
                }
                for (var f = 0; f < 600; f++)
                    warm.Update(1f / 60f, null);
            }
            catch (Exception ex)
            {
                log.Verbose($"CrowdMover: warm-up failed: {ex.Message}");
            }
        });
    }
}
