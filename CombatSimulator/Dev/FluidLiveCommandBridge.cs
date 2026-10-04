// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
#if DEV_EXPERIMENTAL
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Dev;

/// <summary>Dev-only bounded inbox; invokes existing fluid commands on the real game's Framework thread.</summary>
internal sealed class FluidLiveCommandBridge
{
    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "on", "off", "stop", "clear", "status", "trace", "surface", "surfacebody", "surfacebodyraw", "confirm-lip",
        "material", "materialoff", "refraction", "normals", "refractionpath", "refractionoffset", "inspect", "inspectdepth",
        "inspectnormal", "inspectoff", "probe",
    };
    private readonly string path;
    private readonly Action<string> execute;
    private readonly IPluginLog log;
    private long nextPoll;
    private string lastId = string.Empty;

    public FluidLiveCommandBridge(string directory, Action<string> execute, IPluginLog log)
    {
        path = Path.Combine(directory, "fluid-live-command.json");
        this.execute = execute; this.log = log;
        log.Info($"Fluid live diagnostics inbox (DEV only): {path}");
    }

    public void Tick()
    {
        var now = Environment.TickCount64;
        if (now < nextPoll) return;
        nextPoll = now + 500;
        if (!File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Fluid diagnostic request exceeds 4096 bytes");
            var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(path));
            if (request == null || string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 80 ||
                request.Id == lastId || request.ExpiresUtc <= DateTimeOffset.UtcNow ||
                request.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(2) || !Commands.Contains(request.Command))
            {
                log.Warning("Fluid live diagnostics rejected an expired, duplicate or unsupported request");
                return;
            }
            // Mark before executing: a throwing command must not execute again.
            lastId = request.Id;
            log.Info($"Fluid live diagnostics execute [{request.Id}]: {request.Command}");
            execute(request.Command);
            log.Info($"Fluid live diagnostics completed [{request.Id}]");
        }
        catch (Exception ex) { log.Error(ex, "Fluid live diagnostics request failed"); }
        finally
        {
            try { File.Delete(path); }
            catch (IOException ex) { log.Warning(ex, "Fluid live diagnostics could not clear its inbox"); }
            catch (UnauthorizedAccessException ex) { log.Warning(ex, "Fluid live diagnostics could not clear its inbox"); }
        }
    }

    private sealed class Request
    {
        public string Id { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public DateTimeOffset ExpiresUtc { get; set; }
    }
}
#endif
