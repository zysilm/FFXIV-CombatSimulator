// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;

namespace AdvancedRagdoll.Hosting;

/// <summary>
/// The settings the ragdoll reads (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §6), one copy per controller,
/// refreshed once at the start of every render frame from <see cref="IRagdollSettingsSource"/>. Everything
/// in the frame, the physics worker threads included, reads this copy; a setting changed in the middle of
/// a frame takes effect at the next one. Never saved: the user's configuration stays with the host (the
/// plugin's Configuration, whose saved types do not change assembly; the two types below are this library's
/// copies of two of them).
///
/// The property names are those of the plugin's Configuration on purpose: the recorder writes
/// them by name (format version 2 files name the same 34 properties). Stage A2a has a single per-frame
/// copy; the split into an activation snapshot and live switches (§6) narrows it later.
/// </summary>
public sealed class RagdollSettings
{
    public List<string> DismemberPocBones { get; set; } = new();
    public bool EnableCombatCompanions { get; set; }
    public bool EnableNpcDeathRagdoll { get; set; }
    public bool ExtendTerrainDetection { get; set; }
    public GuidedCollapseOptions GuidedCollapse { get; set; } = new();
    public bool KoStripPhysicsDropClothing { get; set; }
    public bool NpcCollisionActive { get; set; }
    public bool NpcRagdollFineDetail { get; set; }
    public bool PartyCompanionDeathRagdoll { get; set; }
    public float RagdollActivationDelay { get; set; }
    public bool RagdollAnatomicalRom { get; set; }
    public bool RagdollExactGroundCollision { get; set; }
    public bool RagdollHairBodyMesh { get; set; }
    public bool RagdollHairPhysics { get; set; }
    public int RagdollMeshCacheRevision { get; set; }
    public bool RagdollMeshCollisionLod { get; set; }
    public int RagdollMeshContactQuality { get; set; }
    public float RagdollMeshLodToleranceMm { get; set; }
    public bool RagdollMeshVolumes { get; set; }
    public bool RagdollNpcCollisionAutoSize { get; set; }
    public RagdollNpcCollisionMode RagdollNpcCollisionMode { get; set; }
    public float RagdollNpcCollisionScale { get; set; }
    public string RagdollSoftTissueBonePrefixes { get; set; } = string.Empty;
    public bool RagdollSoftTissueCollision { get; set; }
    public bool RagdollSoftTissueModBones { get; set; }
    public int RagdollSoftTissueScope { get; set; }
    public float RagdollSquashIntensity { get; set; }
    public bool RagdollSquashStretch { get; set; }
    public int RagdollTriggerMode { get; set; }
    public bool RagdollVerboseLog { get; set; }
    public bool RagdollWaterBuoyancy { get; set; }
    public int RagdollWaterLiquid { get; set; }
    public bool WeaponBodyMeshContacts { get; set; }
    public bool WeaponDropMeshCollision { get; set; }
}


/// <summary>The guided collapse switch (the host's saved settings type stays with the host; the recorder writes this
/// one as JSON, which is the same text for the same value).</summary>
public sealed class GuidedCollapseOptions
{
    /// <summary>Knee buckle on death (the knee power-loss pattern, fixed at <see cref="GuidedCollapseKneePowerLossSettings"/>'s defaults).</summary>
    public bool Enabled { get; set; }
}

/// <summary>How other characters' colliders are built (the same names and values as the plugin's saved setting).</summary>
public enum RagdollNpcCollisionMode
{
    BoneCapsule = 0,
    Mesh = 2,
}
