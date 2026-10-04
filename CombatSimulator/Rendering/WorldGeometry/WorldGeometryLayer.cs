// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;

namespace CombatSimulator.Rendering.WorldGeometry;

/// <summary>
/// A producer's private triangle-list layer. Latest geometry persists until replaced
/// or cleared; enabling/disabling/disposing this layer never clears another producer.
/// SetEnabled and Dispose should be called from the host's main/plugin thread.
/// </summary>
public sealed class WorldGeometryLayer : IDisposable
{
    private readonly WorldGeometryRenderer owner;
    internal readonly WorldGeometryRenderer.LayerState State;

    internal WorldGeometryLayer(WorldGeometryRenderer owner, WorldGeometryRenderer.LayerState state)
    { this.owner = owner; State = state; }

    public string Name => State.Name;
    public int Capacity => State.Capacity;
    public string Status => owner.GetLayerStatus(State);
    public bool IsSuspended => owner.IsSuspended || State.Disposed;

    public void SetEnabled(bool value) => owner.SetLayerEnabled(State, value);
    public void SubmitFrame(ReadOnlySpan<WorldVertex> vertices, bool testSceneDepth = true,
        bool clipSpace = false, bool diagnostic = false, bool shaded = true)
        => owner.Submit(State, vertices, testSceneDepth, clipSpace, diagnostic, shaded);
    public void Clear() => owner.ClearLayer(State);
    public void Dispose() => owner.RemoveLayer(State);
}
