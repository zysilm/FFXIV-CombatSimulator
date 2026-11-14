// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using AdvancedRagdoll;
using AdvancedRagdoll.Hosting;

namespace CombatSimulator.Animation.Hosting;

/// <summary>The plugin's settings source: copies the user's <see cref="Configuration"/> (stays in the plugin with it).</summary>
internal sealed class ConfigRagdollSettings : IHostSettingsSource
{
    private readonly Configuration config;

    public ConfigRagdollSettings(Configuration config) => this.config = config;

    public bool Refresh(HostSettings s)
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
            s.GuidedCollapse = new GuidedCollapseOptions { Enabled = c.GuidedCollapse.Enabled };
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
        if (s.RagdollNpcCollisionMode != (AdvancedRagdoll.Hosting.RagdollNpcCollisionMode)c.RagdollNpcCollisionMode)
        {
            s.RagdollNpcCollisionMode = (AdvancedRagdoll.Hosting.RagdollNpcCollisionMode)c.RagdollNpcCollisionMode;
            changed = true;
        }
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
