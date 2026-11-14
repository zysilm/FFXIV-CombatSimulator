// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// The ragdoll library's namespaces, for the whole plugin. Until stage A4 the library's types lived in
// CombatSimulator.Animation.*, where plugin code found them without a using directive; these keep that
// code compiling unchanged. A plugin type of the same simple name still wins inside the plugin's own
// namespaces (an enclosing namespace is searched before these directives).
global using AdvancedRagdoll;
global using AdvancedRagdoll.Attachment;
global using AdvancedRagdoll.CollapseProfiles;
global using AdvancedRagdoll.Hair;
global using AdvancedRagdoll.MeshVolume;
global using AdvancedRagdoll.Recording;
global using AdvancedRagdoll.SkeletonProfiles;
global using AdvancedRagdoll.SurfaceProfiles;
global using AdvancedRagdoll.Hosting;
global using AdvancedRagdoll.Bepu;
global using AdvancedRagdoll.WeaponDrop;
