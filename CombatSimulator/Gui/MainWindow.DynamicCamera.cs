using System;
using System.Numerics;
using CombatSimulator.Camera;
using Dalamud.Bindings.ImGui;

namespace CombatSimulator.Gui;

/// <summary>
/// The Dynamic Camera page. It gets its own sidebar tab rather than a third collapsing
/// header under Camera: it has more knobs than Active Cam and Death Cam combined, and
/// burying them under the two features it partly replaces makes all three harder to tune.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] ShoulderSideLabels = { "Auto (opposite the enemy)", "Always left", "Always right" };

    private static readonly (string Label, string BoneName)[] DynCamPivotBones =
    {
        ("Hips (j_kosi)", "j_kosi"),
        ("Lower Spine (j_sebo_a)", "j_sebo_a"),
        ("Mid Spine (j_sebo_b)", "j_sebo_b"),
        ("Upper Spine (j_sebo_c)", "j_sebo_c"),
        ("Neck (j_kubi)", "j_kubi"),
        ("Head (j_kao)", "j_kao"),
    };

    public void DrawDynamicCamSection()
    {
        ImGui.TextWrapped(
            "A directed camera for the simulation: the character is framed off to one side during " +
            "the fight, and when they go down the shot pulls back to hold both the body and whoever " +
            "killed them. You keep full control of the camera throughout — it re-frames around you, " +
            "it never takes the stick away.");
        ImGui.Separator();

        var enabled = config.EnableDynamicCamera;
        if (ImGui.Checkbox("Enable Dynamic Camera##dyncam", ref enabled))
        {
            config.EnableDynamicCamera = enabled;
            config.Save();
        }
        HelpMarker("Runs while the combat simulation is active. Turning this off restores the game's own camera immediately.");

        DrawDynamicCamStatus();

        if (!config.EnableDynamicCamera)
            return;

        ImGui.Separator();
        DrawDynamicCamCombatGroup();
        DrawDynamicCamDeathGroup();
        DrawDynamicCamDebugGroup();
    }

    private void DrawDynamicCamStatus()
    {
        if (config.EnableActiveCamera)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
                "Active Camera is on — Dynamic Camera is standing aside.");
            HelpMarker("Active Camera is an explicit choice about who drives the camera, so Dynamic Camera yields to it completely. " +
                       "Turn Active Camera off (Camera page) to use Dynamic Camera.");
        }

        var phase = dynamicCameraController.CurrentPhase;
        var color = phase switch
        {
            DynamicCameraController.Phase.Combat => new Vector4(0.4f, 1f, 0.5f, 1f),
            DynamicCameraController.Phase.DeathTranslate => new Vector4(1f, 0.75f, 0.35f, 1f),
            DynamicCameraController.Phase.DeathHold => new Vector4(1f, 0.55f, 0.55f, 1f),
            _ => new Vector4(0.6f, 0.6f, 0.6f, 1f),
        };
        ImGui.TextColored(color, $"Status: {dynamicCameraController.StatusText}");
    }

    private void DrawDynamicCamCombatGroup()
    {
        if (!ImGui.CollapsingHeader("Combat Framing##dyncam", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Indent();

        var on = config.DynCamCombatFraming;
        if (ImGui.Checkbox("Enable combat framing##dyncam", ref on))
        {
            config.DynCamCombatFraming = on;
            config.Save();
        }
        HelpMarker("Over-the-shoulder framing while you are alive and fighting. Off = the game's normal camera until you die.");

        if (!config.DynCamCombatFraming)
        {
            ImGui.Unindent();
            return;
        }

        var share = config.DynCamSubjectScreenShare;
        if (ImGui.SliderFloat("Body size on screen##dyncam", ref share, 0.35f, 0.75f, "%.2f"))
        {
            config.DynCamSubjectScreenShare = share;
            config.Save();
        }
        HelpMarker("How much of the screen height your character fills. The camera distance is worked backwards from this, " +
                   "so the body stays the same size whatever is going on.\n\n" +
                   "Scroll-wheel zoom still works and is remembered — this is the baseline it scales.");

        var shoulder = config.DynCamShoulderScreenFrac;
        if (ImGui.SliderFloat("Shoulder offset##dyncam", ref shoulder, 0f, 0.4f, "%.2f"))
        {
            config.DynCamShoulderScreenFrac = shoulder;
            config.Save();
        }
        HelpMarker("How far off-centre the character sits, as a fraction of screen width. 0 puts them dead centre; larger values " +
                   "open up more of the frame for the enemy in front of them.\n\n" +
                   "Measured on screen, not in yalms, so the composition holds the same at every zoom level.");

        var side = Math.Clamp(config.DynCamShoulderSide, 0, ShoulderSideLabels.Length - 1);
        if (ImGui.Combo("Character sits##dyncam", ref side, ShoulderSideLabels, ShoulderSideLabels.Length))
        {
            config.DynCamShoulderSide = side;
            config.Save();
        }
        HelpMarker("Auto keeps the character on the far side from whatever they are fighting, so the enemy always has room. " +
                   "The switch is eased and needs the enemy to be clearly across the frame, so it does not flicker.");

        var crowding = config.DynCamCrowdingRelief;
        if (ImGui.SliderFloat("Crowd relief##dyncam", ref crowding, 0f, 1f, "%.2f"))
        {
            config.DynCamCrowdingRelief = crowding;
            config.Save();
        }
        HelpMarker("Pulls the camera back when several enemies spread wider than the frame can hold. " +
                   "This is the fix for the claustrophobic feeling a tight over-the-shoulder camera gets in a crowd. 0 disables it.");

        var bones = DynCamPivotBones;
        var boneLabels = new string[bones.Length];
        for (var i = 0; i < bones.Length; i++)
            boneLabels[i] = bones[i].Label;

        var boneIdx = 2;
        for (var i = 0; i < bones.Length; i++)
            if (bones[i].BoneName == config.DynCamPivotBoneName) { boneIdx = i; break; }

        if (ImGui.Combo("Framed on##dyncam", ref boneIdx, boneLabels, boneLabels.Length))
        {
            config.DynCamPivotBoneName = bones[boneIdx].BoneName;
            config.Save();
        }
        HelpMarker("Which bone the camera orbits when pulled back. Zooming in blends the aim up toward the head on its own, so " +
                   "this only sets where the shot rests at range.");

        if (ImGui.TreeNode("Smoothing##dyncam"))
        {
            var height = config.DynCamHeightOffset;
            if (ImGui.DragFloat("Height offset##dyncam", ref height, 0.01f, -1f, 2f, "%.2f"))
            {
                config.DynCamHeightOffset = height;
                config.Save();
            }

            var pivotSmooth = config.DynCamPivotSmoothing;
            if (ImGui.SliderFloat("Follow response##dyncam", ref pivotSmooth, 1f, 20f, "%.1f"))
            {
                config.DynCamPivotSmoothing = pivotSmooth;
                config.Save();
            }
            HelpMarker("How quickly the camera follows the body. Higher is snappier.");

            var distSmooth = config.DynCamDistanceSmoothing;
            if (ImGui.SliderFloat("Zoom response##dyncam", ref distSmooth, 0.5f, 12f, "%.1f"))
            {
                config.DynCamDistanceSmoothing = distSmooth;
                config.Save();
            }
            HelpMarker("How quickly the distance settles to the body-size target.");

            var hold = config.DynCamInputHold;
            if (ImGui.SliderFloat("Hands-off time##dyncam", ref hold, 0.2f, 6f, "%.1f s"))
            {
                config.DynCamInputHold = hold;
                config.Save();
            }
            HelpMarker("After you touch the camera, this is how long it waits before resuming its own adjustments. " +
                       "It never fights you — this only paces how soon it starts helping again.");

            ImGui.TreePop();
        }

        ImGui.Unindent();
    }

    private void DrawDynamicCamDeathGroup()
    {
        if (!ImGui.CollapsingHeader("Death Framing##dyncam", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Indent();

        var on = config.DynCamDeathFraming;
        if (ImGui.Checkbox("Enable death framing##dyncam", ref on))
        {
            config.DynCamDeathFraming = on;
            config.Save();
        }
        HelpMarker("When you go down, the camera drops to the ground beside your body — like a photographer lying next to it — " +
                   "and backs away only as far as it must to also hold the enemy that killed you, head to toe.\n\n" +
                   "This replaces the old Death Cam while it is on — running both would mean two camera moves fighting each other.");

        if (config.DynCamDeathFraming && config.EnableDeathCam)
        {
            ImGui.TextColored(new Vector4(0.65f, 0.65f, 0.65f, 1f),
                "Death Cam (Camera page) is suppressed while this is on.");
        }

        if (!config.DynCamDeathFraming)
        {
            ImGui.Unindent();
            return;
        }

        var body = config.DynCamDeathBodyVisibility;
        if (ImGui.SliderFloat("Body in frame##dyncam", ref body, 0.25f, 1f, "%.2f"))
        {
            config.DynCamDeathBodyVisibility = body;
            config.Save();
        }
        HelpMarker("How much of your body is guaranteed to stay in shot.\n\n" +
                   "1.00 — head to feet\n" +
                   "0.50 — roughly half the body\n" +
                   "0.25 — head and chest only\n\n" +
                   "Asking for less lets the camera sit closer and tighter.");

        var bodyBand = config.DynCamDeathBodyBand;
        if (ImGui.SliderFloat("Body position in frame##dyncam", ref bodyBand, -0.85f, 0.3f, "%.2f"))
        {
            config.DynCamDeathBodyBand = bodyBand;
            config.Save();
        }
        HelpMarker("Where your body sits on screen: −0.85 is hard against the bottom edge, 0 is dead centre. Low values are " +
                   "what make the shot read as a knockdown.\n\n" +
                   "The camera's height above the ground is worked out from this (together with the angle below) every frame — " +
                   "you say where the body should be, it finds the height. A terrain probe keeps it out of the floor.");

        var angle = config.DynCamDeathAngle;
        if (ImGui.SliderFloat("Camera angle##dyncam", ref angle, -0.35f, 0.45f, "%.2f rad"))
        {
            config.DynCamDeathAngle = angle;
            config.Save();
        }
        HelpMarker("Which way the lens is tipped.\n\n" +
                   "Negative — flat on the ground, looking up past your body at the killer standing over it. Dramatic, but " +
                   "little ground in shot.\n" +
                   "Positive — propped up on the elbows, looking down. Shows the ground and reads more as a body on the floor.\n\n" +
                   "Tipping further down needs more height and distance to still fit the killer; the shot works that out for you, " +
                   "so expect it to pull back as you raise this.\n\n" +
                   "Dragging the camera up and down during the shot nudges this live.");

        var closeUp = config.DynCamDeathCloseUpDistance;
        if (ImGui.SliderFloat("Close-up distance##dyncam", ref closeUp, 1.2f, 6f, "%.1f y"))
        {
            config.DynCamDeathCloseUpDistance = closeUp;
            config.Save();
        }
        HelpMarker("How close to your body the shot wants to sit. It only backs off from this when the killer would not " +
                   "otherwise fit in frame — so this is the tightest the shot will ever be, not a fixed distance.");

        var duration = config.DynCamDeathTranslateDuration;
        if (ImGui.SliderFloat("Move duration##dyncam", ref duration, 0.2f, 6f, "%.1f s"))
        {
            config.DynCamDeathTranslateDuration = duration;
            config.Save();
        }
        HelpMarker("How long the camera takes to travel from wherever it was into the death composition.");

        if (ImGui.TreeNode("Advanced##dyncamdeath"))
        {
            var margin = config.DynCamDeathSafeMargin;
            if (ImGui.SliderFloat("Edge margin##dyncam", ref margin, 0.02f, 0.3f, "%.2f"))
            {
                config.DynCamDeathSafeMargin = margin;
                config.Save();
            }
            HelpMarker("Clear space kept between the subjects and the edge of the screen.");

            var fovMin = config.DynCamDeathFovMin;
            if (ImGui.SliderFloat("Narrowest lens##dyncam", ref fovMin, 0.3f, 1.2f, "%.2f rad"))
            {
                config.DynCamDeathFovMin = fovMin;
                config.Save();
            }

            var fovMax = config.DynCamDeathFovMax;
            if (ImGui.SliderFloat("Widest lens##dyncam", ref fovMax, 0.5f, 2f, "%.2f rad"))
            {
                config.DynCamDeathFovMax = fovMax;
                config.Save();
            }
            HelpMarker("The shot widens the lens before it backs the camera away, so it can stay down close to the body even " +
                       "when the killer is standing right over it.");

            var maxDist = config.DynCamDeathMaxDistance;
            if (ImGui.SliderFloat("Give-up distance##dyncam", ref maxDist, 8f, 60f, "%.0f y"))
            {
                config.DynCamDeathMaxDistance = maxDist;
                config.Save();
            }
            HelpMarker("If framing the killer would need the camera further away than this, it stops trying to hold them: " +
                       "first their legs, then them entirely, falling back to a shot of just your body.");

            var zoomMax = config.DynCamDeathZoomMax;
            if (ImGui.SliderFloat("Zoom-out headroom##dyncam", ref zoomMax, 1f, 5f, "%.1fx"))
            {
                config.DynCamDeathZoomMax = zoomMax;
                config.Save();
            }
            HelpMarker("How far past the framed distance you may pull the camera back with the scroll wheel.");

            var noCollide = config.DynCamDeathDisableCollision;
            if (ImGui.Checkbox("Ignore terrain collision##dyncam", ref noCollide))
            {
                config.DynCamDeathDisableCollision = noCollide;
                config.Save();
            }
            HelpMarker("The death shot sits low and can graze the ground; this stops the game shoving the camera in to avoid it.");

            ImGui.TreePop();
        }

        ImGui.Unindent();
    }

    private void DrawDynamicCamDebugGroup()
    {
        if (!ImGui.CollapsingHeader("Debug##dyncam"))
            return;

        ImGui.Indent();

        var overlay = config.DynCamDebugOverlay;
        if (ImGui.Checkbox("Show framing overlay##dyncam", ref overlay))
        {
            config.DynCamDebugOverlay = overlay;
            config.Save();
        }
        HelpMarker("Draws the safe frame and every point the shot is required to keep visible, plus what the solver came up with.\n\n" +
                   "Each point is plotted twice: green from the game's own projection, magenta from the framing maths. " +
                   "They must sit on top of each other — if they do not, the framing is working from a wrong idea of the camera.");

        ImGui.Separator();
        if (ImGui.Button("Reset All to Defaults##dyncam"))
            config.ResetDynamicCameraDefaults();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Restore every option on this page. Leaves Enable Dynamic Camera alone — resetting it would switch " +
                             "the feature off and take the page with it.");

        ImGui.Unindent();
    }

    /// <summary>Floating toolbar for tuning the framing in-game without the settings window in the way.</summary>
    public void DrawDynamicCamToolbar()
    {
        if (!ImGui.Begin("Dynamic Cam", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var enabled = config.EnableDynamicCamera;
        if (ImGui.Checkbox("##dcEnable", ref enabled))
        {
            config.EnableDynamicCamera = enabled;
            config.Save();
        }

        ImGui.SameLine();
        ImGui.Text("Body");
        ImGui.SameLine();
        var share = config.DynCamSubjectScreenShare;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragFloat("##dcShare", ref share, 0.005f, 0.35f, 0.75f, "%.2f"))
        {
            config.DynCamSubjectScreenShare = share;
            config.Save();
        }

        ImGui.SameLine();
        ImGui.Text("Shoulder");
        ImGui.SameLine();
        var shoulder = config.DynCamShoulderScreenFrac;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragFloat("##dcShoulder", ref shoulder, 0.005f, 0f, 0.4f, "%.2f"))
        {
            config.DynCamShoulderScreenFrac = shoulder;
            config.Save();
        }

        ImGui.SameLine();
        ImGui.Text("KO body");
        ImGui.SameLine();
        var body = config.DynCamDeathBodyVisibility;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragFloat("##dcBody", ref body, 0.01f, 0.25f, 1f, "%.2f"))
        {
            config.DynCamDeathBodyVisibility = body;
            config.Save();
        }

        ImGui.SameLine();
        ImGui.Text("KO body pos");
        ImGui.SameLine();
        var bodyBand = config.DynCamDeathBodyBand;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragFloat("##dcBand", ref bodyBand, 0.01f, -0.85f, 0.3f, "%.2f"))
        {
            config.DynCamDeathBodyBand = bodyBand;
            config.Save();
        }

        ImGui.SameLine();
        ImGui.Text("KO angle");
        ImGui.SameLine();
        var angle = config.DynCamDeathAngle;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragFloat("##dcAngle", ref angle, 0.01f, -0.35f, 0.45f, "%.2f"))
        {
            config.DynCamDeathAngle = angle;
            config.Save();
        }

        ImGui.SameLine();
        ImGui.Text("KO dist");
        ImGui.SameLine();
        var closeUp = config.DynCamDeathCloseUpDistance;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragFloat("##dcCloseUp", ref closeUp, 0.05f, 1.2f, 6f, "%.1f"))
        {
            config.DynCamDeathCloseUpDistance = closeUp;
            config.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"| {dynamicCameraController.StatusText}");

        ImGui.End();
    }
}
