// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;

namespace CombatSimulator.Animation.Hosting;

/// <summary>
/// The settings the ragdoll reads (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §6), one copy per controller,
/// refreshed once at the start of every render frame from <see cref="IRagdollSettingsSource"/>. Everything
/// in the frame, the physics worker threads included, reads this copy; a setting changed in the middle of
/// a frame takes effect at the next one. Never saved: the user's configuration stays in
/// <see cref="Configuration"/> (whose saved types do not move).
///
/// The property names and types are those of <see cref="Configuration"/> on purpose: the recorder writes
/// them by name (format version 2 files name the same 34 properties). Stage A2a has a single per-frame
/// copy; the split into an activation snapshot and live switches (§6) narrows it later.
/// </summary>
public sealed class RagdollSettings
{
    public List<string> DismemberPocBones { get; set; } = new();
    public bool EnableCombatCompanions { get; set; }
    public bool EnableNpcDeathRagdoll { get; set; }
    public bool ExtendTerrainDetection { get; set; }
    public GuidedCollapseSettings GuidedCollapse { get; set; } = new();
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

/// <summary>The plugin's settings source: copies the user's <see cref="Configuration"/>.</summary>
public sealed class ConfigRagdollSettings : IRagdollSettingsSource
{
    private readonly Configuration config;

    public ConfigRagdollSettings(Configuration config) => this.config = config;

    public bool Refresh(RagdollSettings s)
    {
        var c = config;
        var changed = false;
        // A list is replaced, never edited, so a reader that holds the previous list keeps a stable one.
        if (!SameItems(s.DismemberPocBones, c.DismemberPocBones))
        {
            s.DismemberPocBones = c.DismemberPocBones == null ? null! : new List<string>(c.DismemberPocBones);
            changed = true;
        }
        if (s.GuidedCollapse.Enabled != c.GuidedCollapse.Enabled)
        {
            // GuidedCollapseSettings has this one property; a new one must be copied here as well.
            s.GuidedCollapse = new GuidedCollapseSettings { Enabled = c.GuidedCollapse.Enabled };
            changed = true;
        }
        if (s.EnableCombatCompanions != c.EnableCombatCompanions) { s.EnableCombatCompanions = c.EnableCombatCompanions; changed = true; }
        if (s.EnableNpcDeathRagdoll != c.EnableNpcDeathRagdoll) { s.EnableNpcDeathRagdoll = c.EnableNpcDeathRagdoll; changed = true; }
        if (s.ExtendTerrainDetection != c.ExtendTerrainDetection) { s.ExtendTerrainDetection = c.ExtendTerrainDetection; changed = true; }
        if (s.KoStripPhysicsDropClothing != c.KoStripPhysicsDropClothing) { s.KoStripPhysicsDropClothing = c.KoStripPhysicsDropClothing; changed = true; }
        if (s.NpcCollisionActive != c.NpcCollisionActive) { s.NpcCollisionActive = c.NpcCollisionActive; changed = true; }
        if (s.NpcRagdollFineDetail != c.NpcRagdollFineDetail) { s.NpcRagdollFineDetail = c.NpcRagdollFineDetail; changed = true; }
        if (s.PartyCompanionDeathRagdoll != c.PartyCompanionDeathRagdoll) { s.PartyCompanionDeathRagdoll = c.PartyCompanionDeathRagdoll; changed = true; }
        if (Bits(s.RagdollActivationDelay) != Bits(c.RagdollActivationDelay)) { s.RagdollActivationDelay = c.RagdollActivationDelay; changed = true; }
        if (s.RagdollAnatomicalRom != c.RagdollAnatomicalRom) { s.RagdollAnatomicalRom = c.RagdollAnatomicalRom; changed = true; }
        if (s.RagdollExactGroundCollision != c.RagdollExactGroundCollision) { s.RagdollExactGroundCollision = c.RagdollExactGroundCollision; changed = true; }
        if (s.RagdollHairBodyMesh != c.RagdollHairBodyMesh) { s.RagdollHairBodyMesh = c.RagdollHairBodyMesh; changed = true; }
        if (s.RagdollHairPhysics != c.RagdollHairPhysics) { s.RagdollHairPhysics = c.RagdollHairPhysics; changed = true; }
        if (s.RagdollMeshCacheRevision != c.RagdollMeshCacheRevision) { s.RagdollMeshCacheRevision = c.RagdollMeshCacheRevision; changed = true; }
        if (s.RagdollMeshCollisionLod != c.RagdollMeshCollisionLod) { s.RagdollMeshCollisionLod = c.RagdollMeshCollisionLod; changed = true; }
        if (s.RagdollMeshContactQuality != c.RagdollMeshContactQuality) { s.RagdollMeshContactQuality = c.RagdollMeshContactQuality; changed = true; }
        if (Bits(s.RagdollMeshLodToleranceMm) != Bits(c.RagdollMeshLodToleranceMm)) { s.RagdollMeshLodToleranceMm = c.RagdollMeshLodToleranceMm; changed = true; }
        if (s.RagdollMeshVolumes != c.RagdollMeshVolumes) { s.RagdollMeshVolumes = c.RagdollMeshVolumes; changed = true; }
        if (s.RagdollNpcCollisionAutoSize != c.RagdollNpcCollisionAutoSize) { s.RagdollNpcCollisionAutoSize = c.RagdollNpcCollisionAutoSize; changed = true; }
        if (s.RagdollNpcCollisionMode != c.RagdollNpcCollisionMode) { s.RagdollNpcCollisionMode = c.RagdollNpcCollisionMode; changed = true; }
        if (Bits(s.RagdollNpcCollisionScale) != Bits(c.RagdollNpcCollisionScale)) { s.RagdollNpcCollisionScale = c.RagdollNpcCollisionScale; changed = true; }
        if (!string.Equals(s.RagdollSoftTissueBonePrefixes, c.RagdollSoftTissueBonePrefixes, System.StringComparison.Ordinal))
        {
            s.RagdollSoftTissueBonePrefixes = c.RagdollSoftTissueBonePrefixes;
            changed = true;
        }
        if (s.RagdollSoftTissueCollision != c.RagdollSoftTissueCollision) { s.RagdollSoftTissueCollision = c.RagdollSoftTissueCollision; changed = true; }
        if (s.RagdollSoftTissueModBones != c.RagdollSoftTissueModBones) { s.RagdollSoftTissueModBones = c.RagdollSoftTissueModBones; changed = true; }
        if (s.RagdollSoftTissueScope != c.RagdollSoftTissueScope) { s.RagdollSoftTissueScope = c.RagdollSoftTissueScope; changed = true; }
        if (Bits(s.RagdollSquashIntensity) != Bits(c.RagdollSquashIntensity)) { s.RagdollSquashIntensity = c.RagdollSquashIntensity; changed = true; }
        if (s.RagdollSquashStretch != c.RagdollSquashStretch) { s.RagdollSquashStretch = c.RagdollSquashStretch; changed = true; }
        if (s.RagdollTriggerMode != c.RagdollTriggerMode) { s.RagdollTriggerMode = c.RagdollTriggerMode; changed = true; }
        if (s.RagdollVerboseLog != c.RagdollVerboseLog) { s.RagdollVerboseLog = c.RagdollVerboseLog; changed = true; }
        if (s.RagdollWaterBuoyancy != c.RagdollWaterBuoyancy) { s.RagdollWaterBuoyancy = c.RagdollWaterBuoyancy; changed = true; }
        if (s.RagdollWaterLiquid != c.RagdollWaterLiquid) { s.RagdollWaterLiquid = c.RagdollWaterLiquid; changed = true; }
        if (s.WeaponBodyMeshContacts != c.WeaponBodyMeshContacts) { s.WeaponBodyMeshContacts = c.WeaponBodyMeshContacts; changed = true; }
        if (s.WeaponDropMeshCollision != c.WeaponDropMeshCollision) { s.WeaponDropMeshCollision = c.WeaponDropMeshCollision; changed = true; }
        return changed;
    }

    // Bitwise, so a NaN setting (never equal to itself) does not count as a change every frame.
    private static int Bits(float value) => System.BitConverter.SingleToInt32Bits(value);

    private static bool SameItems(List<string>? held, List<string>? now)
    {
        if (ReferenceEquals(held, now)) return true;
        if (held == null || now == null || held.Count != now.Count) return false;
        for (var i = 0; i < held.Count; i++)
            if (!string.Equals(held[i], now[i], System.StringComparison.Ordinal)) return false;
        return true;
    }
}
