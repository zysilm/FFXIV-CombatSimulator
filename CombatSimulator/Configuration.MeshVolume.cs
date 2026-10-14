// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
namespace CombatSimulator;

public partial class Configuration
{
    /// <summary>Current loaded visible triangle surfaces replace structural ragdoll proxies.
    /// Unsupported regions retain explicit legacy fallbacks and report their source diagnostics.</summary>
    public bool RagdollMeshVolumes { get; set; } = false;
    public int RagdollMeshContactQuality { get; set; } = 50;
    public bool RagdollMeshParallelPhysics { get; set; } = true;
    public bool RagdollMeshCollisionLod { get; set; } = false;
    public float RagdollMeshLodToleranceMm { get; set; } = 3f;
    [Newtonsoft.Json.JsonIgnore]
    public int RagdollMeshCacheRevision { get; private set; }
    public void RefreshRagdollMeshCache() => RagdollMeshCacheRevision = unchecked(RagdollMeshCacheRevision + 1);
}
