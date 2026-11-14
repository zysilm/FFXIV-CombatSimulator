// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CombatSimulator.Animation.Recording;
using CombatSimulator.Animation.Hosting;

namespace CombatSimulator.Animation;

/// <summary>
/// Switch and bookkeeping for ragdoll recordings (docs/REFACTOR_PLAN.md, stage A0). While a session
/// is on, every ragdoll activation writes its own file under the plugin configuration directory,
/// sub-folder <c>ragdoll-rec/</c>. Controlled by <c>/combatsim ragdollrec start|stop|status</c>;
/// there is no setting and no UI. Off by default and after every plugin load.
/// </summary>
internal static class RagdollRecordingSession
{
    // Growth guards. A file stops at whichever comes first. The two byte limits are enforced by the writer
    // for every record: the record that would cross one is not written and nothing but the End record
    // follows it. So a file holds at most MaxBytesPerFile and a session at most MaxBytesPerSession, plus one
    // End record (well under 1 KiB) per file. The time limit is looked at once per render frame.
    public const double MaxSecondsPerFile = 300;
    /// <summary>The time limit actually applied: the offline replay (Tests/RagdollReplay), which runs slower than
    /// the game, lifts it so its re-recording is not cut short.</summary>
    internal static double SecondsLimit = MaxSecondsPerFile;
    public const long MaxBytesPerFile = 512L * 1024 * 1024;
    public const long MaxBytesPerSession = 4L * 1024 * 1024 * 1024;
    public const int MaxFilesPerSession = 200;

    internal sealed class RecordingFile
    {
        public required RagdollRecordingWriter Writer { get; init; }
        public required string Path { get; init; }
        public required int Index { get; init; }
        public long StartedAt { get; init; }
        public int RenderFrames;
        public int PhysicsFrames;
        public int FixedSteps;
        public bool Closed;
        public double Seconds => Stopwatch.GetElapsedTime(StartedAt).TotalSeconds;
    }

    private static readonly object Gate = new();
    private static readonly List<RecordingFile> Open = new();
    private static volatile bool enabled;
    private static string directory = string.Empty;
    private static IRagdollLog? log;
    private static string sessionId = string.Empty;
    private static int filesOpened;
    private static int filesClosed;
    private static long bytesClosed;
    private static bool capLogged;
    private static RagdollRecordingBudget budget = new(MaxBytesPerSession);

    /// <summary>True once this session's byte limit refused a record: every open file stops there.</summary>
    public static bool SessionBytesExhausted => budget.Exhausted;

    public static string SessionBytesReason => $"session size limit of {MaxBytesPerSession / (1024 * 1024)} MiB reached";

    /// <summary>The single check on the activation path while recording is off.</summary>
    public static bool Enabled => enabled;
    public static string SessionId => sessionId;

    public static void Configure(string recordingDirectory, IRagdollLog pluginLog)
    {
        directory = recordingDirectory;
        log = pluginLog;
    }

    public static string Start()
    {
        lock (Gate)
        {
            if (string.IsNullOrEmpty(directory)) return "recording directory is not configured";
            if (enabled) return "already on. " + StatusLocked();
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                return $"cannot create {directory}: {ex.Message}";
            }
            sessionId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            filesOpened = filesClosed = 0;
            bytesClosed = 0;
            capLogged = false;
            budget = new RagdollRecordingBudget(MaxBytesPerSession);
            enabled = true;
            log?.Info($"RagdollRec: session {sessionId} started; each ragdoll activation writes one file under {directory} " +
                      $"(a file stops after {MaxSecondsPerFile:F0} s or {MaxBytesPerFile / (1024 * 1024)} MiB; " +
                      $"a session opens at most {MaxFilesPerSession} files and writes at most {MaxBytesPerSession / (1024 * 1024)} MiB in all: " +
                      "when that is reached every open file is closed).");
            return $"on. Files go to {directory}";
        }
    }

    public static string Stop()
    {
        List<RecordingFile> toClose;
        lock (Gate)
        {
            if (!enabled && Open.Count == 0) return "already off. " + StatusLocked();
            enabled = false;
            toClose = new List<RecordingFile>(Open);
        }
        foreach (var file in toClose)
            Close(file, "session stopped");
        lock (Gate)
        {
            log?.Info($"RagdollRec: session {sessionId} stopped; {filesClosed} file(s), {bytesClosed} bytes.");
            return "off. " + StatusLocked();
        }
    }

    public static string Status()
    {
        lock (Gate) return (enabled ? "on. " : "off. ") + StatusLocked();
    }

    private static string StatusLocked()
    {
        long bytes = bytesClosed;
        foreach (var file in Open) bytes += file.Writer.BytesWritten;
        return $"This session: {filesOpened} file(s), {bytes} bytes ({bytes / (1024.0 * 1024.0):F1} MiB), {Open.Count} still open. Folder: {directory}";
    }

    /// <summary>Opens the file for one activation, or null when off, over the session limits or on an I/O error.</summary>
    public static RecordingFile? TryOpen(bool localPlayer, uint entityId)
    {
        if (!enabled) return null;
        lock (Gate)
        {
            if (!enabled) return null;
            var bytes = budget.Used;
            if (filesOpened >= MaxFilesPerSession || budget.Exhausted || bytes >= MaxBytesPerSession)
            {
                if (!capLogged)
                {
                    capLogged = true;
                    log?.Info($"RagdollRec: session limit reached ({filesOpened} files, {bytes} bytes); no more files are opened. " +
                              "Run /combatsim ragdollrec stop, then start again for a new session.");
                }
                return null;
            }

            var index = filesOpened + 1;
            var name = string.Create(CultureInfo.InvariantCulture,
                $"{sessionId}-{index:000}-{(localPlayer ? "player" : "npc")}-{entityId:X8}{RagdollRecordingFormat.FileExtension}");
            var path = System.IO.Path.Combine(directory, name);
            try
            {
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                var opened = new RecordingFile
                {
                    Writer = new RagdollRecordingWriter(stream, budget, MaxBytesPerFile),
                    Path = path,
                    Index = index,
                    StartedAt = Stopwatch.GetTimestamp(),
                };
                filesOpened = index;
                Open.Add(opened);
                log?.Info($"RagdollRec: opened {path} ({(localPlayer ? "local player" : "other actor")}, entity {entityId:X8}).");
                return opened;
            }
            catch (Exception ex)
            {
                log?.Warning(ex, $"RagdollRec: cannot open {path}; this activation is not recorded.");
                return null;
            }
        }
    }

    /// <summary>
    /// Called by a recorded controller that found the session's byte limit reached: closes every open
    /// file (none of them accepts another record) and logs the reason once.
    /// </summary>
    public static void CloseAllAtSessionLimit()
    {
        List<RecordingFile> toClose;
        lock (Gate)
        {
            if (!budget.Exhausted) return;
            toClose = new List<RecordingFile>(Open);
            if (!capLogged)
            {
                capLogged = true;
                log?.Info($"RagdollRec: {SessionBytesReason} ({filesOpened} files, {budget.Used} bytes); " +
                          $"closing the {toClose.Count} open file(s), no more files are opened. " +
                          "Run /combatsim ragdollrec stop, then start again for a new session.");
            }
        }
        foreach (var file in toClose)
            Close(file, SessionBytesReason);
    }

    /// <summary>Writes the end record, closes the file and logs one line. Safe to call twice.</summary>
    public static void Close(RecordingFile file, string reason)
    {
        lock (Gate)
        {
            if (file.Closed) return;
            file.Closed = true;
            Open.Remove(file);
            var writer = file.Writer;
            var seconds = file.Seconds;
            using (writer.Begin(RecordType.End))
            {
                writer.Str(reason);
                writer.I32(file.RenderFrames);
                writer.I32(file.PhysicsFrames);
                writer.I32(file.FixedSteps);
                writer.I64(writer.RecordCount + 1);
                writer.F64(seconds);
            }
            writer.Dispose();
            filesClosed++;
            bytesClosed += writer.BytesWritten;
            var failure = writer.Failed ? $" WRITE FAILED: {writer.FailureMessage}" : string.Empty;
            if (writer.DiscardedRecords > 0)
                failure += $" {writer.DiscardedRecords} partial record(s) were discarded (each left a note in the file).";
            log?.Info($"RagdollRec: closed {file.Path} ({reason}): {file.RenderFrames} render frames, " +
                      $"{file.PhysicsFrames} physics frames, {file.FixedSteps} steps, {writer.BytesWritten} bytes, {seconds:F1} s.{failure}");
        }
    }
}
