// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler;
using FFXIVClientStructs.FFXIV.Client.UI;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace CombatSimulator.Camera;

/// <summary>
/// Read-only scene diagnostic (<c>/combatsim sceneprobe [start|stop]</c>): what the game looks like right now — the
/// conditions, the local player, the object table, the cameras, the input, the running cutscene and this plugin's own
/// state — written to dalamud.log as <c>SceneProbe:</c> lines. Used to judge whether a simulated battle could run while
/// a cutscene replay plays (inn → The Unending Journey).
/// <para>
/// It reads fields only: no write to game memory, no game function called (singleton getters aside). Objects are
/// reached only through Dalamud's object table; a character's fields are read only when the table says the entry is
/// a character. <c>start</c> takes one snapshot a second until <c>stop</c>, at most <see cref="MaxSeconds"/> seconds:
/// the first sample and every sample where the set of objects changed list every object, the others only the local
/// player and the client-object range (where cutscene actors and this plugin's own actors live).
/// </para>
/// </summary>
public sealed unsafe class SceneProbe : IDisposable
{
    private const int MaxSeconds = 120;
    private const int MaxObjectLines = 120;
    private const int MaxVisibleAddons = 48;
    private const uint SimulatedEntityIdFloor = 0xF0000000;   // Safety/FakeActorGuard.cs
    private const uint InvalidEntityId = 0xE0000000;

    // The object table by index (GameObjectManager.Objects.IndexSorted, 819 entries): 0–199 the world's characters,
    // 200–448 client-created objects (ClientObjectManager's 249 slots: cutscene actors conventionally 200–239, the
    // game's own preview characters 240–248, and this plugin's spawned actors), 449 and up event / housing objects.
    private const int ClientRangeStart = 200;
    private const int CutsceneRangeEnd = 240;
    private const int PreviewRangeEnd = 249;
    private const int ClientRangeEnd = 449;

    private static readonly string[] WatchedAddons =
    {
        "_ActionBar", "_ActionCross", "NamePlate", "_TargetInfo", "_MainCommand", "_NaviMap", "ChatLog",
        "CutSceneReplay", "CutScenePlayer", "Talk", "FadeMiddle", "FadeBack",
    };

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Func<string> pluginState;

    // Filled by Dalamud (IDalamudPluginInterface.Inject); either may stay null, and is then reported as n/a.
    [PluginService] private IKeyState? KeyState { get; set; }
    [PluginService] private IGamepadState? Gamepad { get; set; }

    private bool sampling;
    private long samplingStartTicks;
    private long nextSampleTicks;
    private int samples;
    private long lastObjectSignature;

    private Vector3? lastPlayerPosition;
    private long lastPlayerTicks;
    private uint lastCutsceneId;
    private long cutsceneSeenTicks;

    public SceneProbe(IDalamudPluginInterface pluginInterface, IFramework framework, IClientState clientState,
        IObjectTable objects, ICondition condition, IGameGui gameGui, IDataManager data, IPluginLog log,
        Func<string> pluginState)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.objects = objects;
        this.condition = condition;
        this.gameGui = gameGui;
        this.data = data;
        this.log = log;
        this.pluginState = pluginState;
        try { pluginInterface.Inject(this); }
        catch (Exception ex) { log.Warning(ex, "SceneProbe: key / gamepad state not available."); }
    }

    public bool Sampling => sampling;

    /// <summary>One full snapshot now.</summary>
    public string Snapshot()
    {
        var lines = WriteSnapshot(fullObjects: true, label: "snapshot");
        return $"snapshot written to dalamud.log ({lines} lines, prefix \"SceneProbe:\")";
    }

    public string Start()
    {
        if (sampling) return $"already sampling ({samples} samples so far)";
        sampling = true;
        samples = 0;
        lastObjectSignature = 0;
        samplingStartTicks = Environment.TickCount64;
        nextSampleTicks = samplingStartTicks;
        framework.Update += OnUpdate;
        log.Info($"SceneProbe: sampling started (one snapshot a second, at most {MaxSeconds} s).");
        return $"sampling once a second into dalamud.log (stops by itself after {MaxSeconds} s; /combatsim sceneprobe stop)";
    }

    public string Stop() => StopSampling("stopped");

    private string StopSampling(string why)
    {
        if (!sampling) return "not sampling";
        sampling = false;
        framework.Update -= OnUpdate;
        var seconds = (Environment.TickCount64 - samplingStartTicks) / 1000f;
        log.Info($"SceneProbe: sampling {why}, {samples} samples in {F1(seconds)} s.");
        return $"{why}, {samples} samples in {F1(seconds)} s (dalamud.log, prefix \"SceneProbe:\")";
    }

    private void OnUpdate(IFramework fw)
    {
        if (!sampling) return;
        try
        {
            var now = Environment.TickCount64;
            if (now - samplingStartTicks > MaxSeconds * 1000L)
            {
                var text = StopSampling("time limit reached");
                try { Core.Services.ChatGui.Print($"[CombatSim] Scene probe: {text}"); } catch { }
                return;
            }
            if (now < nextSampleTicks) return;
            nextSampleTicks = now + 1000;

            var signature = ObjectSignature();
            var full = samples == 0 || signature != lastObjectSignature;
            lastObjectSignature = signature;
            samples++;
            WriteSnapshot(full, $"sample {samples} t={F1((now - samplingStartTicks) / 1000f)}s{(full ? " (all objects)" : "")}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "SceneProbe: sample failed; sampling stopped.");
            StopSampling("failed");
        }
    }

    // ---- The snapshot ----

    private int WriteSnapshot(bool fullObjects, string label)
    {
        var lines = new List<string>(160);
        Section(lines, "state", () => StateLine(label));
        Section(lines, "cond", ConditionLine);
        Section(lines, "player", PlayerLine);
        Section(lines, "camera", CameraLine);
        Section(lines, "input", InputLine);
        Section(lines, "ui", UiLine);
        Section(lines, "timeline", TimelineLine);
        Section(lines, "plugin", () => "plugin: " + pluginState());
        try { ObjectLines(lines, fullObjects); }
        catch (Exception ex) { lines.Add($"objects: failed ({ex.GetType().Name}: {ex.Message})"); }
        foreach (var line in lines) log.Info("SceneProbe: " + line);
        return lines.Count;
    }

    private static void Section(List<string> lines, string name, Func<string> build)
    {
        try { lines.Add(build()); }
        catch (Exception ex) { lines.Add($"{name}: failed ({ex.GetType().Name}: {ex.Message})"); }
    }

    private string StateLine(string label)
    {
        var sb = new StringBuilder(256);
        sb.Append("=== ").Append(label).Append(" === ");
        var territory = clientState.TerritoryType;
        sb.Append("territory=").Append(territory);
        try
        {
            var row = data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRowOrDefault(territory);
            if (row != null)
            {
                var place = row.Value.PlaceName.ValueNullable?.Name.ExtractText();
                sb.Append(" \"").Append(place).Append("\" (").Append(row.Value.Name.ExtractText()).Append(", use ")
                  .Append(row.Value.TerritoryIntendedUse.RowId).Append(')');
            }
        }
        catch { }
        sb.Append(" map=").Append(clientState.MapId)
          .Append(" loggedIn=").Append(B(clientState.IsLoggedIn))
          .Append(" gpose=").Append(B(clientState.IsGPosing))
          .Append(" pvp=").Append(B(clientState.IsPvP));
        var env = EnvManager.Instance();
        if (env != null)
            sb.Append(" weather=").Append(env->ActiveWeather).Append(" dayTime=").Append(F1(env->DayTimeSeconds / 3600f)).Append('h');
        return sb.ToString();
    }

    private string ConditionLine()
    {
        var sb = new StringBuilder(256);
        sb.Append("cond:");
        var seen = new HashSet<int>();
        var any = false;
        foreach (var flag in Enum.GetValues<ConditionFlag>())
        {
            var index = (int)flag;
            if (index <= 0 || index >= condition.MaxEntries || !seen.Add(index)) continue;
            if (!condition[index]) continue;
            sb.Append(' ').Append(flag).Append('(').Append(index).Append(')');
            any = true;
        }
        // Entries the enum has no name for.
        for (var index = 1; index < condition.MaxEntries; index++)
        {
            if (seen.Contains(index) || !condition[index]) continue;
            sb.Append(" #").Append(index);
            any = true;
        }
        if (!any) sb.Append(" none");
        return sb.ToString();
    }

    private string PlayerLine()
    {
        var player = objects.LocalPlayer;
        if (player == null || player.Address == nint.Zero) return "player: none (no local player object)";
        var sb = new StringBuilder(320);
        var c = (Character*)player.Address;
        sb.Append("player: addr=0x").Append(player.Address.ToString("X")).Append(" idx=").Append(player.ObjectIndex)
          .Append(player.ObjectIndex == 0 ? " (real, index 0)" : " (NOT index 0)")
          .Append(" pos=").Append(V(player.Position)).Append(" rot=").Append(F2(player.Rotation))
          .Append(' ').Append(DrawText(&c->GameObject))
          .Append(" mode=").Append(c->Mode).Append('/').Append(c->ModeParam)
          .Append(" job=").Append(c->ClassJob).Append(" lv=").Append(c->Level)
          .Append(" hp=").Append(c->Health).Append('/').Append(c->MaxHealth)
          .Append(" tl=").Append(TimelineText(c))
          .Append(" alpha=").Append(F2(c->Alpha));

        // The same character elsewhere in the table (a cutscene copies the player into the client range).
        var name = player.Name.TextValue;
        var copies = new List<string>();
        foreach (var o in objects)
        {
            if (o == null || o.Address == nint.Zero || o.Address == player.Address) continue;
            if (o.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc) continue;
            if (o.ObjectIndex < ClientRangeStart || o.Name.TextValue != name) continue;
            copies.Add(o.ObjectIndex.ToString(CultureInfo.InvariantCulture));
        }
        sb.Append(" copies=").Append(copies.Count == 0 ? "none" : "[" + string.Join(",", copies) + "]");

        var control = Control.Instance();
        if (control != null)
            sb.Append(" control.LocalPlayer=").Append(control->LocalPlayer == null ? "null"
                : (nint)control->LocalPlayer == player.Address ? "same" : "0x" + ((nint)control->LocalPlayer).ToString("X"));
        return sb.ToString();
    }

    private string CameraLine()
    {
        var sb = new StringBuilder(320);
        sb.Append("camera:");
        var manager = GameCameraManager.Instance();
        if (manager == null) return sb.Append(" no camera manager").ToString();
        var index = manager->ActiveCameraIndex;
        var (kind, pointer) = index switch
        {
            0 => ("game", (nint)manager->Camera),
            1 => ("lowcut(cutscene)", (nint)manager->LowCutCamera),
            2 => ("lobby", (nint)manager->LobbyCamera),
            3 => ("spectator", (nint)manager->SpectatorCamera),
            4 => ("aiming", (nint)manager->AimingCamera),
            _ => ("unknown", nint.Zero),
        };
        sb.Append(" active=").Append(index).Append(' ').Append(kind).Append(" 0x").Append(pointer.ToString("X"))
          .Append(" prev=").Append(manager->PreviousCameraIndex);

        nint activeScene = nint.Zero;
        if (pointer != nint.Zero)
        {
            var scene = &((CameraBase*)pointer)->SceneCamera;
            activeScene = (nint)scene;
            sb.Append(" pos=").Append(V(scene->Position)).Append(" look=").Append(V(scene->LookAtVector));
            var render = scene->RenderCamera;
            if (render != null)
                sb.Append(" fov=").Append(F2(render->FoV)).Append(" near=").Append(F2(render->NearPlane));
        }

        var sceneManager = SceneCameraManager.Instance();
        if (sceneManager != null)
        {
            var current = (nint)sceneManager->CurrentCamera;
            sb.Append(" scene.idx=").Append(sceneManager->CameraIndex).Append(" scene.current=")
              .Append(current == nint.Zero ? "null" : current == activeScene ? "active" : "0x" + current.ToString("X") + "(other)");
        }

        var game = manager->Camera;
        if (game != null)
        {
            sb.Append(" | game: mode=").Append(game->ControlMode).Append(" zoom=").Append(game->ZoomMode)
              .Append(" dist=").Append(F2(game->Distance)).Append(" fov=").Append(F2(game->FoV))
              .Append(" dirH=").Append(F2(game->DirH)).Append(" dirV=").Append(F2(game->DirV))
              .Append(" eventAuto=").Append(B(game->IsEventCameraAutoControl))
              .Append(" pos=").Append(V(game->CameraBase.SceneCamera.Position));
        }
        return sb.ToString();
    }

    private string InputLine()
    {
        var sb = new StringBuilder(256);
        sb.Append("input: keys=");
        if (KeyState == null) sb.Append("n/a");
        else
        {
            var any = false;
            foreach (var (key, label) in MoveKeys)
            {
                bool down;
                try { down = KeyState.IsVirtualKeyValid(key) && KeyState[key]; } catch { down = false; }
                if (!down) continue;
                sb.Append(label);
                any = true;
            }
            if (!any) sb.Append('-');
        }
        sb.Append(" pad.left=");
        if (Gamepad == null || Gamepad.GamepadInputAddress == nint.Zero) sb.Append("n/a");
        else sb.Append('(').Append(F2(Gamepad.LeftStick.X)).Append(',').Append(F2(Gamepad.LeftStick.Y)).Append(')');

        var player = objects.LocalPlayer;
        var now = Environment.TickCount64;
        if (player != null && player.Address != nint.Zero)
        {
            var position = player.Position;
            if (lastPlayerPosition is { } last && now > lastPlayerTicks)
            {
                var dt = (now - lastPlayerTicks) / 1000f;
                sb.Append(" playerMoved=").Append(F2(Vector3.Distance(position, last))).Append("m/").Append(F1(dt)).Append('s');
            }
            else sb.Append(" playerMoved=first-sample");
            lastPlayerPosition = position;
            lastPlayerTicks = now;
        }
        else
        {
            sb.Append(" playerMoved=no-player");
            lastPlayerPosition = null;
        }

        var control = Control.Instance();
        if (control != null) sb.Append(" walking=").Append(B(control->IsWalking));

        // The conditions that take the controls away.
        sb.Append(" blocked=");
        var blocked = false;
        foreach (var flag in BlockingConditions)
        {
            if (!condition[flag]) continue;
            sb.Append(blocked ? "," : "").Append(flag);
            blocked = true;
        }
        if (!blocked) sb.Append("no");
        return sb.ToString();
    }

    private string UiLine()
    {
        var sb = new StringBuilder(512);
        sb.Append("ui: hidden=").Append(B(gameGui.GameUiHidden));
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null) return sb.Append(" no unit manager").ToString();
        sb.Append(" mgrFlags=").Append(manager->Flags).Append(" uiFlags=0x").Append(((uint)manager->UiFlags).ToString("X"));

        var watched = new Dictionary<string, string>();
        var visible = new List<string>();
        var visibleCount = 0;
        ref var list = ref manager->AllLoadedUnitsList;
        var count = Math.Min((int)list.Count, list.Entries.Length);
        for (var i = 0; i < count; i++)
        {
            var unit = list.Entries[i].Value;
            if (unit == null) continue;
            var name = unit->NameString;
            var shown = unit->IsVisible;
            if (Array.IndexOf(WatchedAddons, name) >= 0) watched[name] = shown ? "shown" : "hidden";
            if (!shown) continue;
            visibleCount++;
            if (visible.Count < MaxVisibleAddons) visible.Add(name);
        }
        sb.Append(" loaded=").Append(count).Append(" |");
        foreach (var name in WatchedAddons)
            sb.Append(' ').Append(name).Append('=').Append(watched.TryGetValue(name, out var state) ? state : "absent");
        sb.Append(" | visible(").Append(visibleCount).Append(")=").Append(string.Join(",", visible));
        if (visibleCount > visible.Count) sb.Append(",…");
        return sb.ToString();
    }

    private string TimelineLine()
    {
        var sb = new StringBuilder(320);
        sb.Append("timeline:");
        uint cutsceneId = 0;
        if (ScheduleManagement.Addresses.Instance.Value != 0)
        {
            var schedule = ScheduleManagement.Instance();
            if (schedule == null) sb.Append(" schedule=null");
            else if (schedule->CutSceneController == null) sb.Append(" cutsceneController=null");
            else
            {
                cutsceneId = schedule->CutSceneController->CutsceneId;
                sb.Append(" cutsceneController=0x").Append(((nint)schedule->CutSceneController).ToString("X"))
                  .Append(" cutscene=").Append(cutsceneId);
                try
                {
                    var row = data.GetExcelSheet<Lumina.Excel.Sheets.Cutscene>()?.GetRowOrDefault(cutsceneId);
                    if (row != null) sb.Append(" path=").Append(row.Value.Path.ExtractText());
                }
                catch { }
            }
        }
        else sb.Append(" schedule=unresolved");

        // No playback clock is read (none is exposed as a plain field); this is the time since the probe first saw the id.
        var now = Environment.TickCount64;
        if (cutsceneId != lastCutsceneId)
        {
            lastCutsceneId = cutsceneId;
            cutsceneSeenTicks = now;
        }
        if (cutsceneId != 0) sb.Append(" seenFor=").Append(F1((now - cutsceneSeenTicks) / 1000f)).Append('s');

        var events = EventFramework.Instance();
        if (events != null)
        {
            sb.Append(" | event: load=").Append(events->LoadState)
              .Append(" scene=").Append(events->Scene).Append(" sceneFlags=0x").Append(events->SceneFlags.ToString("X"))
              .Append(" sceneObj=0x").Append(((ulong)events->SceneGameObjectId).ToString("X"));
            EventStateText(sb, " s1", ref events->EventState1);
            EventStateText(sb, " s2", ref events->EventState2);
        }
        return sb.ToString();
    }

    private static void EventStateText(StringBuilder sb, string label, ref EventState state)
    {
        sb.Append(label).Append("(id=0x").Append(((uint)state.EventId).ToString("X"))
          .Append(" obj=0x").Append(((ulong)state.ObjectId).ToString("X"))
          .Append(" cur=").Append(state.CurrentEvent).Append('/').Append(state.CurrentEventParam)
          .Append(" occ=").Append(state.OccupiedConditionId)
          .Append(" f=0x").Append(state.Flags.ToString("X")).Append(')');
    }

    // ---- Objects ----

    private long ObjectSignature()
    {
        long hash = 17;
        foreach (var o in objects)
        {
            if (o == null || o.Address == nint.Zero) continue;
            hash = unchecked(hash * 31 + o.ObjectIndex);
            hash = unchecked(hash * 31 + o.Address);
            hash = unchecked(hash * 31 + o.BaseId);
        }
        return hash;
    }

    private void ObjectLines(List<string> lines, bool full)
    {
        var all = new List<IGameObject>(128);
        var perRange = new int[5];
        var simulated = 0;
        foreach (var o in objects)
        {
            if (o == null || o.Address == nint.Zero) continue;
            all.Add(o);
            perRange[RangeOf(o.ObjectIndex)]++;
            if (o.EntityId >= SimulatedEntityIdFloor) simulated++;
        }

        var summary = new StringBuilder(160);
        summary.Append("objects: ").Append(all.Count).Append(" | world(0-199)=").Append(perRange[0])
               .Append(" cutscene(200-239)=").Append(perRange[1])
               .Append(" preview(240-248)=").Append(perRange[2])
               .Append(" client(249-448)=").Append(perRange[3])
               .Append(" event(449+)=").Append(perRange[4])
               .Append(" | simulated(ours)=").Append(simulated);
        var clients = ClientObjectManager.Instance();
        if (clients != null)
        {
            var slots = clients->BattleCharas;
            var used = 0;
            for (var i = 0; i < slots.Length; i++)
                if (slots[i].BattleChara != null) used++;
            summary.Append(" clientSlots=").Append(used).Append('/').Append(slots.Length);
        }
        lines.Add(summary.ToString());

        // The local player and the client range first (the stage), then the world's characters, then the rest.
        var playerAddress = objects.LocalPlayer?.Address ?? nint.Zero;
        all.Sort((a, b) =>
        {
            var pa = Priority(a, playerAddress);
            var pb = Priority(b, playerAddress);
            return pa != pb ? pa.CompareTo(pb) : a.ObjectIndex.CompareTo(b.ObjectIndex);
        });
        var written = 0;
        var skipped = 0;
        foreach (var o in all)
        {
            if (!full && Priority(o, playerAddress) > 1) { skipped++; continue; }
            if (written >= MaxObjectLines) { skipped++; continue; }
            try { lines.Add(ObjectLine(o, playerAddress)); }
            catch (Exception ex) { lines.Add($" [{o.ObjectIndex}] failed ({ex.GetType().Name})"); }
            written++;
        }
        if (skipped > 0)
            lines.Add(full ? $" … {skipped} more objects not listed (limit {MaxObjectLines})"
                           : $" … {skipped} objects outside the client range not listed in this sample");
    }

    private static int RangeOf(int index) =>
        index < ClientRangeStart ? 0 : index < CutsceneRangeEnd ? 1 : index < PreviewRangeEnd ? 2 : index < ClientRangeEnd ? 3 : 4;

    private static int Priority(IGameObject o, nint playerAddress)
    {
        if (o.Address == playerAddress) return 0;
        var range = RangeOf(o.ObjectIndex);
        if (range is 1 or 2 or 3) return 1;
        if (range == 0) return o is ICharacter ? 2 : 3;
        return 4;
    }

    private string ObjectLine(IGameObject o, nint playerAddress)
    {
        var g = (GameObject*)o.Address;
        var index = o.ObjectIndex;
        var range = RangeOf(index);
        var sb = new StringBuilder(320);
        sb.Append(" [").Append(index).Append("] ")
          .Append(range switch { 0 => "world", 1 => "cut", 2 => "preview", 3 => "client", _ => "event" });

        // What the entry looks like: ours, the player, a cutscene actor (client range, not ours), a copy of the player.
        var entityId = o.EntityId;
        if (o.Address == playerAddress) sb.Append(" SELF");
        if (entityId >= SimulatedEntityIdFloor) sb.Append(" SIM");
        else if (range is 1 or 3) sb.Append(" ACTOR");
        if (entityId == InvalidEntityId || entityId == 0) sb.Append(" noEid");

        sb.Append(' ').Append(o.ObjectKind).Append('/').Append(o.SubKind)
          .Append(" \"").Append(o.Name.TextValue).Append('"')
          .Append(" eid=").Append(entityId.ToString("X"))
          .Append(" base=").Append(o.BaseId)
          .Append(" pos=").Append(V(o.Position)).Append(" rot=").Append(F2(o.Rotation))
          .Append(" scale=").Append(F2(g->Scale))
          .Append(' ').Append(DrawText(g));

        if (o is ICharacter)
        {
            var c = (Character*)o.Address;
            var modelId = c->ModelContainer.ModelCharaId;
            sb.Append(" model=").Append(ModelTypeText(modelId)).Append('#').Append(modelId);
            if (modelId == 0)
            {
                ref var customize = ref c->DrawData.CustomizeData;
                sb.Append(" race=").Append(customize.Race).Append(" sex=").Append(customize.Sex)
                  .Append(" tribe=").Append(customize.Tribe).Append(" height=").Append(customize.Height);
            }
            sb.Append(" mscale=").Append(F2(c->ModelScale))
              .Append(" mode=").Append(c->Mode).Append('/').Append(c->ModeParam)
              .Append(" tl=").Append(TimelineText(c))
              .Append(" hp=").Append(c->Health).Append('/').Append(c->MaxHealth)
              .Append(" job=").Append(c->ClassJob)
              .Append(" alpha=").Append(F2(c->Alpha));
        }
        return sb.ToString();
    }

    /// <summary>The draw state of an object the object table handed out: draw object, visibility, render and target flags.</summary>
    private static string DrawText(GameObject* g)
    {
        var draw = g->DrawObject;
        var text = draw == null ? "draw=null" : draw->IsVisible ? "draw=visible" : "draw=hidden";
        return text + " rf=0x" + ((uint)g->RenderFlags).ToString("X")
                    + " tgt=0x" + ((byte)g->TargetableStatus).ToString("X")
                    + ((g->TargetableStatus & ObjectTargetableFlags.IsTargetable) != 0 ? "(targetable)" : "(untargetable)");
    }

    private static string TimelineText(Character* c)
    {
        var ids = c->Timeline.TimelineSequencer.TimelineIds;
        var sb = new StringBuilder(32);
        sb.Append(ids.Length > 0 ? ids[0] : 0);
        if (ids.Length > 1 && ids[1] != 0) sb.Append('+').Append(ids[1]);
        if (c->Timeline.BaseOverride != 0) sb.Append("/base").Append(c->Timeline.BaseOverride);
        return sb.ToString();
    }

    private string ModelTypeText(int modelCharaId)
    {
        if (modelCharaId == 0) return "Human";
        try
        {
            var row = data.GetExcelSheet<Lumina.Excel.Sheets.ModelChara>()?.GetRowOrDefault((uint)modelCharaId);
            if (row == null) return "?";
            return row.Value.Type switch
            {
                1 => "Human",
                2 => "Demihuman",
                3 => "Monster",
                4 => "Weapon",
                var other => "type" + other,
            };
        }
        catch { return "?"; }
    }

    // ---- Tables and formatting ----

    private static readonly (VirtualKey Key, string Label)[] MoveKeys =
    {
        (VirtualKey.W, "W"), (VirtualKey.A, "A"), (VirtualKey.S, "S"), (VirtualKey.D, "D"),
        (VirtualKey.Q, "Q"), (VirtualKey.E, "E"), (VirtualKey.SPACE, "Space"),
        (VirtualKey.UP, "Up"), (VirtualKey.DOWN, "Down"), (VirtualKey.LEFT, "Left"), (VirtualKey.RIGHT, "Right"),
    };

    private static readonly ConditionFlag[] BlockingConditions =
    {
        ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78,
        ConditionFlag.Occupied, ConditionFlag.Occupied30, ConditionFlag.Occupied33, ConditionFlag.Occupied38,
        ConditionFlag.Occupied39, ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.OccupiedSummoningBell, ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
    };

    private static string B(bool value) => value ? "1" : "0";
    private static string F1(float value) => value.ToString("F1", CultureInfo.InvariantCulture);
    private static string F2(float value) => value.ToString("F2", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => $"({F2(v.X)},{F2(v.Y)},{F2(v.Z)})";
    private static string V(FFXIVClientStructs.FFXIV.Common.Math.Vector3 v) => $"({F2(v.X)},{F2(v.Y)},{F2(v.Z)})";

    public void Dispose()
    {
        if (sampling) StopSampling("plugin unloaded");
    }
}
