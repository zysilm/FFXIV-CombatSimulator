using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Companions;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Recipes;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace CombatSimulator.Gui;

// Experimental dev toolbars (Grab / KO Strip / Monster / Hold) + their dev-only helpers and tables.
// Partial of MainWindow so the dev GUI keeps access to MainWindow internals while physically living
// in the experimental module folder (kept out of the public source set).
public partial class MainWindow
{
    public void DrawGrabToolbar(Dev.VictorySequenceController victorySequenceController)
    {
        if (!ImGui.Begin("Grab", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var stages = config.VictorySequenceStages;
        var idx = victorySequenceController.CurrentStageIndex;

        if (!victorySequenceController.IsActive || idx < 0 || idx >= stages.Count)
        {
            ImGui.TextDisabled("No active stage");
            ImGui.End();
            return;
        }

        var s = stages[idx];
        ImGui.Text($"Stage {idx}");

        ImGui.SameLine();
        var grab = s.GrabEnabled;
        if (ImGui.Checkbox("Grab##tb", ref grab))
        { s.GrabEnabled = grab; config.Save(); }

        if (s.GrabEnabled)
        {
            ImGui.SameLine();
            ImGui.Text("F");
            ImGui.SameLine();
            var gf = s.GrabForce;
            ImGui.SetNextItemWidth(60);
            if (ImGui.DragFloat("##tbGF", ref gf, 10f, 10, 5000, "%.0f"))
            { s.GrabForce = gf; config.Save(); }

            ImGui.SameLine();
            ImGui.Text("S");
            ImGui.SameLine();
            var gs = s.GrabSpeed;
            ImGui.SetNextItemWidth(60);
            if (ImGui.DragFloat("##tbGS", ref gs, 1f, 1, 200, "%.0f"))
            { s.GrabSpeed = gs; config.Save(); }

            ImGui.SameLine();
            ImGui.Text("Hz");
            ImGui.SameLine();
            var gsf = s.GrabSpringFreq;
            ImGui.SetNextItemWidth(60);
            if (ImGui.DragFloat("##tbGHz", ref gsf, 5f, 10, 500, "%.0f"))
            { s.GrabSpringFreq = gsf; config.Save(); }
        }

        ImGui.End();
    }

    private static readonly (string Bone, string Label, float DefaultHeight)[] HoldAnchorBones =
    {
        ("j_kosi",     "Pelvis",       0.92f),
        ("j_sebo_a",   "Low Spine",    1.05f),
        ("j_sebo_b",   "Mid Spine",    1.15f),
        ("j_sebo_c",   "Chest",        1.25f),
        ("j_kubi",     "Neck",         1.45f),
        ("j_kao",      "Head",         1.60f),
        ("j_te_l",     "L Hand",       1.05f),
        ("j_te_r",     "R Hand",       1.05f),
        ("j_asi_d_l",  "L Foot",       0.05f),
        ("j_asi_d_r",  "R Foot",       0.05f),
    };

    private static readonly string[] HoldGrabNpcBones    = { "j_te_r", "j_te_l" };
    private static readonly string[] HoldGrabPlayerBones = { "j_kubi", "j_sebo_c", "j_kosi", "j_kao", "j_ude_b_r", "j_ude_b_l" };

    // Detailed bone mode: real-time scan of all player skeleton bones
    private string[]? holdDetailedBoneCache;

    // Emote cache for the attack-mode dropdown ("Atk" first, then alphabetical emotes)
    private List<(uint EmoteId, string Name)>? holdAttackEmoteCache;
    private string[]? holdAttackEmoteNames;

    private void EnsureHoldAttackEmoteCache()
    {
        if (holdAttackEmoteCache != null) return;
        var items = new List<(uint, string)> { (0u, "Atk") };
        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
            if (sheet != null)
            {
                var emotes = new List<(uint, string)>();
                foreach (var emote in sheet)
                {
                    var name = emote.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        emotes.Add((emote.RowId, name));
                }
                emotes.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
                items.AddRange(emotes);
            }
        }
        catch { }
        holdAttackEmoteCache = items;
        holdAttackEmoteNames = items.ConvertAll(e => e.Item2).ToArray();
    }

    private (ushort Loop, ushort Intro) ResolveHoldEmoteTimelines(uint emoteId)
    {
        if (emoteId == 0) return (0, 0);
        try
        {
            var row = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()?.GetRow(emoteId);
            if (row.HasValue)
                return ((ushort)row.Value.ActionTimeline[0].RowId, (ushort)row.Value.ActionTimeline[1].RowId);
        }
        catch { }
        return (0, 0);
    }

    private record struct HoldPreset(string Name, string Bone, float Height,
        bool BindArms = false, float ArmSpread = 0.8f, float ArmHeight = 1.2f, bool WallPin = false);

    private static readonly HoldPreset[] HoldPresets =
    {
        new("Kneel",       "j_kosi",   0.35f),
        new("Suspend",     "j_kubi",   2.0f),
        new("Interrogate", "j_kosi",   1.02f),
        new("Wall",        "j_sebo_b", 1.2f,  WallPin: true),
    };

    public void DrawKoStripToolbar(Dev.KoStripController ctrl)
    {
        if (!ImGui.Begin("KO Strip", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var enabled = config.KoStripEnabled;
        if (ImGui.Checkbox("Strip on KO##kostrip", ref enabled))
        {
            config.KoStripEnabled = enabled;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When you are knocked out, visually unequip the selected slots\n(swap to smallclothes). Your character only. Visual only.");

        ImGui.Separator();
        ImGui.TextDisabled("Slots");

        KoStripSlotCheckbox("Head", () => config.KoStripHead, v => config.KoStripHead = v);
        ImGui.SameLine();
        KoStripSlotCheckbox("Body", () => config.KoStripBody, v => config.KoStripBody = v);
        ImGui.SameLine();
        KoStripSlotCheckbox("Hands", () => config.KoStripHands, v => config.KoStripHands = v);
        KoStripSlotCheckbox("Legs", () => config.KoStripLegs, v => config.KoStripLegs = v);
        ImGui.SameLine();
        KoStripSlotCheckbox("Feet", () => config.KoStripFeet, v => config.KoStripFeet = v);

        ImGui.TextDisabled("Accessories");
        KoStripSlotCheckbox("Ears", () => config.KoStripEars, v => config.KoStripEars = v);
        ImGui.SameLine();
        KoStripSlotCheckbox("Neck", () => config.KoStripNeck, v => config.KoStripNeck = v);
        ImGui.SameLine();
        KoStripSlotCheckbox("Wrists", () => config.KoStripWrists, v => config.KoStripWrists = v);
        KoStripSlotCheckbox("R.Finger", () => config.KoStripRFinger, v => config.KoStripRFinger = v);
        ImGui.SameLine();
        KoStripSlotCheckbox("L.Finger", () => config.KoStripLFinger, v => config.KoStripLFinger = v);

        ImGui.Separator();

        // ── Manual test buttons (ignore the apply-to toggles) ────────────────
        if (ImGui.Button("Strip Now##kostrip"))
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player != null) ctrl.StripNow(player.Address);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Strip your character right now (test).");

        ImGui.End();
    }

    private void KoStripSlotCheckbox(string label, Func<bool> get, Action<bool> set)
    {
        var v = get();
        if (ImGui.Checkbox($"{label}##kostripSlot", ref v))
        {
            set(v);
            config.Save();
        }
    }

    private static readonly (string Name, uint Id, uint NameId)[] MonsterModels =
    {
        ("Bat", 38, 38),
        ("Cactuar", 3, 3),
        ("Hog", 15, 15),
        ("Imp", 21, 21),
        ("Flytrap", 23, 23),
        ("Tortoise", 34, 34),
        ("Wisp", 45, 45),
        ("Myconid", 48, 48),
        ("Striking Dummy", 541, 541),
    };

    private static readonly (string Name, int Key)[] MonsterAttackKeys =
    {
        ("Y", 0x59),
        ("F", 0x46),
        ("R", 0x52),
        ("C", 0x43),
        ("X", 0x58),
        ("Space", 0x20),
    };

    private static readonly string[] MonsterStrikePartProfileNames = { "Off", "Sequential", "Random" };

    public void DrawMonsterToolbar(Dev.MonsterModeController ctrl)
    {
        if (!ImGui.Begin("Monster", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var active = ctrl.IsActive;

        var onDeath = config.MonsterSpawnOnDeath;
        if (ImGui.Checkbox("Spawn on death##monster", ref onDeath))
        {
            config.MonsterSpawnOnDeath = onDeath;
            if (onDeath) config.MonsterControlKiller = false; // mutually exclusive
            config.Save();
        }

        var controlKiller = config.MonsterControlKiller;
        if (ImGui.Checkbox("Control killer on death##monster", ref controlKiller))
        {
            config.MonsterControlKiller = controlKiller;
            if (controlKiller)
            {
                config.MonsterSpawnOnDeath = false; // can't spawn while controlling the killer
                ctrl.Despawn();                      // force-despawn any active monster
            }
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("On death, take control of the enemy that just defeated you instead of\nspawning a creature. Same controls. Disables spawning while on.");

        var modelIdx = Array.FindIndex(MonsterModels, m => m.Id == config.MonsterModelId);
        if (modelIdx < 0) modelIdx = 0;
        ImGui.SetNextItemWidth(140);
        if (ImGui.BeginCombo("Model##monster", MonsterModels[modelIdx].Name))
        {
            for (int i = 0; i < MonsterModels.Length; i++)
                if (ImGui.Selectable(MonsterModels[i].Name, i == modelIdx))
                {
                    config.MonsterModelId = MonsterModels[i].Id;
                    config.MonsterModelNameId = MonsterModels[i].NameId;
                    config.Save();
                }
            ImGui.EndCombo();
        }

        ImGui.BeginDisabled(active || config.MonsterControlKiller);
        if (ImGui.Button("Spawn##monster")) ctrl.Spawn();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!active);
        if (ImGui.Button("Despawn##monster")) ctrl.Despawn();
        ImGui.EndDisabled();

        ImGui.BeginDisabled(!active);
        if (ImGui.Button($"Cam: {(ctrl.CameraFollowsMonster ? "Monster" : "Character")}##monster"))
            ctrl.ToggleCamera();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Switch the active camera between following the monster and your character.");

        ImGui.Separator();

        var moveSpeed = config.MonsterMoveSpeed;
        if (ImGui.SliderFloat("Move##monster", ref moveSpeed, 1f, 20f, "%.1f")) { config.MonsterMoveSpeed = moveSpeed; config.Save(); }

        var groundWalk = config.MonsterGroundWalk;
        if (ImGui.Checkbox("Walk on ground (no fly)##monster", ref groundWalk))
        {
            config.MonsterGroundWalk = groundWalk;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clamp the creature to the floor (raycast / navmesh) so it walks instead of flying.\nOff = free flight (Q/E or L2/R2 up/down).");

        ImGui.BeginDisabled(config.MonsterGroundWalk);
        var vSpeed = config.MonsterVerticalSpeed;
        if (ImGui.SliderFloat("Fly##monster", ref vSpeed, 1f, 15f, "%.1f")) { config.MonsterVerticalSpeed = vSpeed; config.Save(); }
        ImGui.EndDisabled();
        var strikePower = config.MonsterStrikePower;
        if (ImGui.SliderFloat("Strike power##monster", ref strikePower, 0.01f, 2f, "%.2f"))
        {
            config.MonsterStrikePower = MathF.Round(strikePower, 2); // 0.01 steps
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How hard the swinging limb flings the body (multiplies the limb's swing speed).");

        var keyIdx = Array.FindIndex(MonsterAttackKeys, k => k.Key == config.MonsterAttackKey);
        if (keyIdx < 0) keyIdx = 0;
        ImGui.SetNextItemWidth(140);
        if (ImGui.BeginCombo("Attack key##monster", MonsterAttackKeys[keyIdx].Name))
        {
            for (int i = 0; i < MonsterAttackKeys.Length; i++)
                if (ImGui.Selectable(MonsterAttackKeys[i].Name, i == keyIdx))
                { config.MonsterAttackKey = MonsterAttackKeys[i].Key; config.Save(); }
            ImGui.EndCombo();
        }

        ImGui.Separator();
        var partProfileIdx = Math.Clamp(config.MonsterStrikePartProfile, 0, MonsterStrikePartProfileNames.Length - 1);
        ImGui.SetNextItemWidth(160);
        if (ImGui.BeginCombo("On-hit parts##monster", MonsterStrikePartProfileNames[partProfileIdx]))
        {
            for (int i = 0; i < MonsterStrikePartProfileNames.Length; i++)
                if (ImGui.Selectable(MonsterStrikePartProfileNames[i], i == partProfileIdx))
                { config.MonsterStrikePartProfile = i; config.Save(); }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A swing that connects in range always sparks the player. This adds progressive part separation:\n" +
                             "Sequential — fore-arms/shins (4 hits) → arms/thighs (4 hits) → head.\n" +
                             "Random — any still-attached part each hit (parent supersedes its child).\n" +
                             "When nothing is left to peel, a further strike releases the hold (if held).");

        if (ImGui.Button("Reset defaults##monster"))
        {
            config.MonsterMoveSpeed = 6f;
            config.MonsterVerticalSpeed = 2.5f;
            config.MonsterStrikePower = 0.1f;
            config.MonsterAttackKey = 0x59; // Y
            config.MonsterModelId = 38;     // Bat
            config.MonsterModelNameId = 38;
            config.MonsterGroundWalk = true;
            config.MonsterStrikePartProfile = 0; // Off
            config.Save();
        }

        ImGui.TextDisabled("WASD = camera-relative move · Q/E up/down");
        ImGui.TextDisabled("Gamepad: left stick move · L2/R2 down/up · Cross attack");
        ImGui.TextDisabled(active ? "Monster: active" : "Monster: none");

        ImGui.End();
    }

    public void DrawHoldToolbar(Dev.BoneHoldTestModeController ctrl)
    {
        if (!ImGui.Begin("Hold", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var active = ctrl.IsActive;

        // ── Row 1: main controls ─────────────────────────────────────────────
        var alreadyDead = ragdollController.IsActive;
        ImGui.BeginDisabled(alreadyDead);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
        if (ImGui.Button("Die##hold"))
            ctrl.TriggerInstantDeath();
        ImGui.PopStyleColor();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(alreadyDead ? "Already dead" : "Instantly kill player (starts combat sim if needed)");

        ImGui.SameLine();
        if (active) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.2f, 0.2f, 1f));
        if (ImGui.Button(active ? "Release##hold" : "Hold##hold"))
        {
            if (active) ctrl.Stop(npcSelector.SelectedNpcs);
            else
            {
                var (eLoop, eIntro) = ResolveHoldEmoteTimelines(config.HoldAttackEmoteId);
                ctrl.SetAttackEmote(eLoop, eIntro);
                ctrl.TryStart(npcSelector.SelectedNpcs,
                    config.HoldAnchorBone, config.HoldStandingHeight,
                    config.HoldNpcAttack, config.HoldAllNpcsAttack, config.HoldApproachDistance,
                    config.HoldShakeEnabled, config.HoldShakeIntensity,
                    config.HoldBindArms, config.HoldArmSpread, config.HoldArmHeight,
                    config.HoldGrabEnabled, config.HoldGrabNpcBone, config.HoldGrabPlayerBone,
                    config.HoldGrabForce, config.HoldGrabFreq);
            }
        }
        if (active) ImGui.PopStyleColor();

        ImGui.SameLine();
        HoldBoneCombo(ctrl, active);
        ImGui.SameLine();
        HoldHeightDrag(ctrl, active);

        // ── Attack ───────────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Attack##holdSec"))
        {
            // Enable checkbox (unlabeled) + emote/attack mode combo
            var atk = config.HoldNpcAttack;
            if (ImGui.Checkbox("##holdAtkEnable", ref atk))
            {
                config.HoldNpcAttack = atk; config.Save();
                if (active) ctrl.SetAttack(atk, config.HoldAllNpcsAttack, npcSelector.SelectedNpcs);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Enable attack / emote");

            ImGui.SameLine();
            EnsureHoldAttackEmoteCache();
            var emoteIdx = holdAttackEmoteCache!.FindIndex(e => e.EmoteId == config.HoldAttackEmoteId);
            if (emoteIdx < 0) emoteIdx = 0;
            ImGui.SetNextItemWidth(130);
            if (ImGui.Combo("##holdAtkMode", ref emoteIdx, holdAttackEmoteNames!, holdAttackEmoteNames!.Length))
            {
                var sel = holdAttackEmoteCache[emoteIdx];
                config.HoldAttackEmoteId = sel.EmoteId;
                config.Save();
                var (loop, intro) = ResolveHoldEmoteTimelines(sel.EmoteId);
                if (active) ctrl.SetAttackEmote(loop, intro);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("\"Atk\" = melee animation; others = looped emote");

            ImGui.SameLine();
            var all = config.HoldAllNpcsAttack;
            if (ImGui.Checkbox("All##hold", ref all))
            {
                config.HoldAllNpcsAttack = all; config.Save();
                if (active) ctrl.SetAttackAll(all);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("All alive NPCs");

            ImGui.SameLine();
            ImGui.Text("D");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(55);
            var dist = config.HoldApproachDistance;
            if (ImGui.DragFloat("##holdD", ref dist, 0.05f, 0.1f, 3.0f, "%.1fm"))
            { config.HoldApproachDistance = dist; config.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Approach distance");
        }

        // ── Bind Arms ────────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Bind##holdSec"))
        {
            var arms = config.HoldBindArms;
            if (ImGui.Checkbox("Arms##hold", ref arms))
            {
                config.HoldBindArms = arms;
                config.Save();
                if (active) ctrl.UpdateArmBind(arms, config.HoldArmSpread, config.HoldArmHeight);
            }

            if (config.HoldBindArms)
            {
                ImGui.SameLine();
                ImGui.Text("Spread");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(60);
                var spread = config.HoldArmSpread;
                if (ImGui.DragFloat("##holdSpread", ref spread, 0.05f, 0.1f, 2.0f, "%.2fm"))
                {
                    config.HoldArmSpread = spread;
                    config.Save();
                    if (active) ctrl.UpdateArmBind(true, config.HoldArmSpread, config.HoldArmHeight);
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Distance between wrists");

                ImGui.SameLine();
                ImGui.Text("H");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(55);
                var armH = config.HoldArmHeight;
                if (ImGui.DragFloat("##holdAH", ref armH, 0.02f, 0.0f, 2.5f, "%.2fm"))
                {
                    config.HoldArmHeight = armH;
                    config.Save();
                    if (active) ctrl.UpdateArmBind(true, config.HoldArmSpread, config.HoldArmHeight);
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Wrist height above ground");
            }
        }

        // ── NPC Grab ─────────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Grab##holdSec"))
        {
            var grab = config.HoldGrabEnabled;
            if (ImGui.Checkbox("##holdGrab", ref grab))
            {
                config.HoldGrabEnabled = grab; config.Save();
                if (active) ctrl.SetGrab(grab, config.HoldGrabNpcBone, config.HoldGrabPlayerBone,
                    config.HoldGrabForce, config.HoldGrabFreq);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pin player bone to NPC hand position");

            ImGui.SameLine();
            // NPC bone combo
            var npcBoneIdx = Array.IndexOf(HoldGrabNpcBones, config.HoldGrabNpcBone);
            if (npcBoneIdx < 0) npcBoneIdx = 0;
            ImGui.SetNextItemWidth(55);
            if (ImGui.Combo("##holdGNpc", ref npcBoneIdx, HoldGrabNpcBones, HoldGrabNpcBones.Length))
            { config.HoldGrabNpcBone = HoldGrabNpcBones[npcBoneIdx]; config.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("NPC bone (hand)");

            ImGui.SameLine();
            ImGui.Text("→");
            ImGui.SameLine();
            // Player bone combo
            var playerBoneIdx = Array.IndexOf(HoldGrabPlayerBones, config.HoldGrabPlayerBone);
            if (playerBoneIdx < 0) playerBoneIdx = 0;
            ImGui.SetNextItemWidth(70);
            if (ImGui.Combo("##holdGPlayer", ref playerBoneIdx, HoldGrabPlayerBones, HoldGrabPlayerBones.Length))
            { config.HoldGrabPlayerBone = HoldGrabPlayerBones[playerBoneIdx]; config.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Player bone to grab");

            ImGui.SameLine();
            ImGui.Text("F");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            var gf = config.HoldGrabForce;
            if (ImGui.DragFloat("##holdGF", ref gf, 10f, 50f, 3000f, "%.0f"))
            { config.HoldGrabForce = gf; config.Save(); }

            ImGui.SameLine();
            ImGui.Text("Hz");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(50);
            var ghz = config.HoldGrabFreq;
            if (ImGui.DragFloat("##holdGHz", ref ghz, 5f, 10f, 300f, "%.0f"))
            { config.HoldGrabFreq = ghz; config.Save(); }
        }

        // ── Impact ───────────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Impact##holdSec"))
        {
            var shake = config.HoldShakeEnabled;
            if (ImGui.Checkbox("Shake##hold", ref shake))
            {
                config.HoldShakeEnabled = shake;
                config.Save();
                if (active) ctrl.SetShake(shake, config.HoldShakeIntensity);
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(55);
            var str = config.HoldShakeIntensity;
            if (ImGui.DragFloat("##holdStr", ref str, 0.1f, 0.5f, 15f, "%.1f"))
            {
                config.HoldShakeIntensity = str;
                config.Save();
                if (active && shake) ctrl.SetShake(true, str);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shake intensity (m/s)");

            ImGui.SameLine();
            ImGui.Text("|");
            ImGui.SameLine();

            if (!active) ImGui.BeginDisabled();
            if (ImGui.Button("←##hold"))  ctrl.Push(0,  -1, 0);
            ImGui.SameLine();
            if (ImGui.Button("→##hold"))  ctrl.Push(0,  +1, 0);
            ImGui.SameLine();
            if (ImGui.Button("↑##hold"))  ctrl.Push(0,   0, 1, speed: 6f);
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.2f, 0.2f, 1f));
            if (ImGui.Button("Fling##hold")) ctrl.Fling();
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Launch upward and release hold");
            if (!active) ImGui.EndDisabled();
        }

        // ── Presets ──────────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Presets##holdSec"))
        {
            for (int i = 0; i < HoldPresets.Length; i++)
            {
                if (i > 0) ImGui.SameLine();
                var p = HoldPresets[i];
                if (ImGui.Button($"{p.Name}##holdPre{i}"))
                {
                    config.HoldAnchorBone     = p.Bone;
                    config.HoldStandingHeight = p.Height;
                    config.HoldBindArms       = p.BindArms;
                    config.HoldArmSpread      = p.ArmSpread;
                    config.HoldArmHeight      = p.ArmHeight;
                    config.Save();
                    if (active)
                    {
                        if (p.WallPin)
                            ctrl.PinToWall(p.Bone, p.Height);
                        else
                            ctrl.UpdateHold(p.Bone, p.Height);
                        ctrl.UpdateArmBind(p.BindArms, p.ArmSpread, p.ArmHeight);
                    }
                }
            }
        }

        ImGui.End();
    }

    private unsafe void HoldBoneCombo(Dev.BoneHoldTestModeController ctrl, bool active)
    {
        // Detailed mode toggle (small checkbox before the combo)
        var detailed = config.HoldDetailedBoneMode;
        ImGui.SetNextItemWidth(16);
        if (ImGui.Checkbox("##holdBoneDetailed", ref detailed))
        {
            config.HoldDetailedBoneMode = detailed;
            config.Save();
            holdDetailedBoneCache = null; // force refresh on next open
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Detailed: show all character bones (real-time)");
        ImGui.SameLine();

        if (!detailed)
        {
            // Normal mode: preset list
            var label = config.HoldAnchorBone;
            var idx   = 0;
            for (int i = 0; i < HoldAnchorBones.Length; i++)
            {
                if (HoldAnchorBones[i].Bone != config.HoldAnchorBone) continue;
                label = HoldAnchorBones[i].Label;
                idx   = i;
                break;
            }
            ImGui.SetNextItemWidth(78);
            if (ImGui.BeginCombo("##holdBone", label))
            {
                for (int i = 0; i < HoldAnchorBones.Length; i++)
                {
                    var sel = i == idx;
                    if (ImGui.Selectable(HoldAnchorBones[i].Label, sel))
                    {
                        config.HoldAnchorBone     = HoldAnchorBones[i].Bone;
                        config.HoldStandingHeight = HoldAnchorBones[i].DefaultHeight;
                        config.Save();
                        if (active) ctrl.UpdateHold(config.HoldAnchorBone, config.HoldStandingHeight);
                    }
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Bone to pin");
        }
        else
        {
            // Detailed mode: scan all bones from player skeleton
            RefreshDetailedBoneCache();
            var bones = holdDetailedBoneCache;
            var current = config.HoldAnchorBone;
            ImGui.SetNextItemWidth(110);
            if (ImGui.BeginCombo("##holdBoneDetail", current))
            {
                if (bones != null)
                {
                    foreach (var bone in bones)
                    {
                        var sel = bone == current;
                        if (ImGui.Selectable(bone, sel))
                        {
                            config.HoldAnchorBone = bone;
                            config.Save();
                            if (active) ctrl.UpdateHold(bone, config.HoldStandingHeight);
                        }
                        if (sel) ImGui.SetItemDefaultFocus();
                    }
                }
                else
                {
                    ImGui.TextDisabled("No skeleton available");
                }
                ImGui.EndCombo();
            }
            else
            {
                // Refresh every time the combo is closed so next open is up to date
                holdDetailedBoneCache = null;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("All skeleton bones (real-time)");
        }
    }

    private unsafe void RefreshDetailedBoneCache()
    {
        if (holdDetailedBoneCache != null) return;
        try
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player == null) return;
            var go      = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
            if (go->DrawObject == null) return;
            var charBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)go->DrawObject;
            var skeleton = charBase->Skeleton;
            if (skeleton == null || skeleton->PartialSkeletonCount < 1) return;
            var partial  = &skeleton->PartialSkeletons[0];
            var pose     = partial->GetHavokPose(0);
            if (pose == null || pose->Skeleton == null) return;
            var havokSkel = pose->Skeleton;
            var count     = havokSkel->Bones.Length;
            var names     = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                var name = havokSkel->Bones[i].Name.String;
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            holdDetailedBoneCache = names.ToArray();
        }
        catch { holdDetailedBoneCache = null; }
    }

    private void HoldHeightDrag(Dev.BoneHoldTestModeController ctrl, bool active)
    {
        ImGui.SetNextItemWidth(60);
        var h = config.HoldStandingHeight;
        if (ImGui.DragFloat("##holdH", ref h, 0.02f, -0.5f, 2.5f, "%.2fm"))
        {
            config.HoldStandingHeight = h;
            config.Save();
            if (active) ctrl.UpdateHold(config.HoldAnchorBone, config.HoldStandingHeight);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Height offset above death position");
    }
}
