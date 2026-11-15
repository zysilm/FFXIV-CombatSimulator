// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using CombatSimulator.Animation;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler;

namespace CombatSimulator.Camera;

/// <summary>
/// Passive calibration probe for the cutscene lens (<c>/combatsim cutprobe start|stop</c>): while the user watches one of
/// the game's own cutscenes, every camera frame is written to a CSV in the plugin's config folder — the render camera's
/// position, look-at, view and projection matrices and FOV fields, the playing cutscene's id (Cutscene sheet row) and the
/// time since it started, and the root and head bones of the characters near the camera. Read-only: it never writes game
/// memory and calls no game function. Used later to settle whether c116 is the vertical or horizontal FOV and what c117
/// is (docs/CUTSCENE_SHOTS.md §5).
/// </summary>
public sealed unsafe class CutsceneProbe : IDisposable
{
    private const int MaxFrames = 36000;   // ~10 min at 60 fps
    private const int MaxActors = 12;
    private const float ActorRadius = 40f;

    private readonly string directory;
    private readonly BoneTransformService bones;
    private readonly IPluginLog log;
    private StreamWriter? writer;
    private string? path;
    private int frames;
    private long startTicks;
    private uint lastCutsceneId;
    private long cutsceneStartTicks;
    private readonly HashSet<uint> cutscenesSeen = new();

    public CutsceneProbe(string directory, BoneTransformService bones, IPluginLog log)
    {
        this.directory = directory;
        this.bones = bones;
        this.log = log;
    }

    public bool Active => writer != null;

    public string Start()
    {
        if (writer != null) return $"already recording to {path} ({frames} frames)";
        try
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, $"cutprobe-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16);
            writer.WriteLine("# CombatSimulator cutscene camera probe. One line per main-camera update, written after the game's update (read-only).");
            writer.WriteLine("# t = seconds since start; cut = ScheduleManagement.CutSceneController.CutsceneId (Cutscene sheet row; 0 = none), cut_t = seconds since that id appeared;");
            writer.WriteLine("# cond = conditions OccupiedInCutSceneEvent/WatchingCutscene/WatchingCutscene78 as 0/1; origin = RenderCamera.Origin; pos/look = Scene camera Position/LookAtVector; v1 = Scene camera Vector_1;");
            writer.WriteLine("# view/proj = RenderCamera ViewMatrix/ProjectionMatrix (M11..M44 row by row); rcfov/rcfov2/aspect/near/far = RenderCamera fields; gamefov = Game.Camera FoV;");
            writer.WriteLine("# actors (nearest to the camera first): index, base id, kind, name, position, rotation, n_root world pos + quat, j_kao world pos + quat.");
            var head = new StringBuilder("t,cut,cut_t,cond,origin_x,origin_y,origin_z,pos_x,pos_y,pos_z,look_x,look_y,look_z,v1_x,v1_y,v1_z");
            for (var i = 1; i <= 4; i++) for (var j = 1; j <= 4; j++) head.Append($",view{i}{j}");
            for (var i = 1; i <= 4; i++) for (var j = 1; j <= 4; j++) head.Append($",proj{i}{j}");
            head.Append(",rcfov,rcfov2,aspect,near,far,gamefov,actors...");
            writer.WriteLine(head.ToString());
            frames = 0;
            startTicks = Environment.TickCount64;
            lastCutsceneId = 0;
            cutscenesSeen.Clear();
            log.Info($"CutProbe: recording to {path}");
            return $"recording to {path}";
        }
        catch (Exception ex)
        {
            writer = null;
            log.Warning(ex, "CutProbe: could not start.");
            return $"could not start: {ex.Message}";
        }
    }

    public string Stop()
    {
        if (writer == null) return "not recording";
        try { writer.Flush(); writer.Dispose(); } catch { }
        writer = null;
        var seen = cutscenesSeen.Count == 0 ? "none" : string.Join(", ", cutscenesSeen);
        log.Info($"CutProbe: stopped, {frames} frames → {path}; cutscene ids seen: {seen}");
        foreach (var id in cutscenesSeen)
        {
            try
            {
                var row = Core.Services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Cutscene>()?.GetRowOrDefault(id);
                if (row != null) log.Info($"CutProbe: cutscene {id} = {row.Value.Path}");
            }
            catch { }
        }
        return $"stopped, {frames} frames in {path} (cutscene ids: {seen})";
    }

    public string Status() => writer == null ? "not recording" : $"recording, {frames} frames → {path}";

    /// <summary>One line for this camera frame. Called after the game's camera update, before anything of ours writes.</summary>
    public void Record(CameraBase* camera)
    {
        if (writer == null || camera == null) return;
        try
        {
            var sc = &camera->SceneCamera;
            var rc = sc->RenderCamera;
            if (rc == null) return;
            var now = Environment.TickCount64;
            var cutId = ReadCutsceneId();
            if (cutId != lastCutsceneId)
            {
                lastCutsceneId = cutId;
                cutsceneStartTicks = now;
                if (cutId != 0 && cutscenesSeen.Add(cutId)) log.Info($"CutProbe: cutscene id {cutId} started (frame {frames}).");
            }
            var cond = Core.Services.Condition;
            var sb = new StringBuilder(1024);
            sb.Append(F((now - startTicks) / 1000f)).Append(',').Append(cutId).Append(',')
              .Append(F(cutId != 0 ? (now - cutsceneStartTicks) / 1000f : 0f)).Append(',')
              .Append(cond[ConditionFlag.OccupiedInCutSceneEvent] ? 1 : 0)
              .Append(cond[ConditionFlag.WatchingCutscene] ? 1 : 0)
              .Append(cond[ConditionFlag.WatchingCutscene78] ? 1 : 0);
            V(sb, rc->Origin.X, rc->Origin.Y, rc->Origin.Z);
            V(sb, sc->Position.X, sc->Position.Y, sc->Position.Z);
            V(sb, sc->LookAtVector.X, sc->LookAtVector.Y, sc->LookAtVector.Z);
            V(sb, sc->Vector_1.X, sc->Vector_1.Y, sc->Vector_1.Z);
            M(sb, rc->ViewMatrix);
            M(sb, rc->ProjectionMatrix);
            var game = (FFXIVClientStructs.FFXIV.Client.Game.Camera*)camera;
            sb.Append(',').Append(F(rc->FoV)).Append(',').Append(F(rc->FoV_2)).Append(',').Append(F(rc->AspectRatio))
              .Append(',').Append(F(rc->NearPlane)).Append(',').Append(F(rc->FarPlane)).Append(',').Append(F(game->FoV));
            AppendActors(sb, new Vector3(rc->Origin.X, rc->Origin.Y, rc->Origin.Z));
            writer.WriteLine(sb.ToString());
            if (++frames >= MaxFrames)
            {
                log.Info("CutProbe: frame limit reached.");
                Stop();
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CutProbe: frame not recorded; stopping.");
            Stop();
        }
    }

    private static uint ReadCutsceneId()
    {
        // Read the singleton only when its address was resolved; a field read, no game function call.
        if (ScheduleManagement.Addresses.Instance.Value == 0) return 0;
        var sm = ScheduleManagement.Instance();
        if (sm == null || sm->CutSceneController == null) return 0;
        return sm->CutSceneController->CutsceneId;
    }

    private void AppendActors(StringBuilder sb, Vector3 eye)
    {
        var list = new List<(float D, IGameObject O)>();
        foreach (var o in Core.Services.ObjectTable)
        {
            if (o == null || o.Address == nint.Zero || o is not ICharacter) continue;
            var d = Vector3.Distance(o.Position, eye);
            if (d <= ActorRadius) list.Add((d, o));
        }
        list.Sort((a, b) => a.D.CompareTo(b.D));
        for (var i = 0; i < list.Count && i < MaxActors; i++)
        {
            var o = list[i].O;
            sb.Append(',').Append(o.ObjectIndex).Append(',').Append(o.BaseId).Append(',').Append(o.ObjectKind)
              .Append(',').Append(o.Name.TextValue.Replace(',', ' ').Replace('"', ' '));
            V(sb, o.Position.X, o.Position.Y, o.Position.Z);
            sb.Append(',').Append(F(o.Rotation));
            Bone(sb, o.Address, "n_root");
            Bone(sb, o.Address, "j_kao");
        }
    }

    private void Bone(StringBuilder sb, nint address, string name)
    {
        var t = bones.GetBoneWorldTransform(address, name);
        if (t is { } b)
        {
            V(sb, b.Position.X, b.Position.Y, b.Position.Z);
            sb.Append(',').Append(F(b.Rotation.X)).Append(',').Append(F(b.Rotation.Y)).Append(',').Append(F(b.Rotation.Z)).Append(',').Append(F(b.Rotation.W));
        }
        else sb.Append(",,,,,,,");
    }

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static void V(StringBuilder sb, float x, float y, float z) =>
        sb.Append(',').Append(F(x)).Append(',').Append(F(y)).Append(',').Append(F(z));

    private static void M(StringBuilder sb, in FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4 m)
    {
        sb.Append(',').Append(F(m.M11)).Append(',').Append(F(m.M12)).Append(',').Append(F(m.M13)).Append(',').Append(F(m.M14));
        sb.Append(',').Append(F(m.M21)).Append(',').Append(F(m.M22)).Append(',').Append(F(m.M23)).Append(',').Append(F(m.M24));
        sb.Append(',').Append(F(m.M31)).Append(',').Append(F(m.M32)).Append(',').Append(F(m.M33)).Append(',').Append(F(m.M34));
        sb.Append(',').Append(F(m.M41)).Append(',').Append(F(m.M42)).Append(',').Append(F(m.M43)).Append(',').Append(F(m.M44));
    }

    public void Dispose()
    {
        if (writer != null) Stop();
    }
}
