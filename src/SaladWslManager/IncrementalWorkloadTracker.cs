using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Generic;

internal static partial class Program
{
    private static readonly object incrementalWorkloadLock = new object();
    private static WorkloadSnapshot incrementalWorkloadSnapshot;
    private static bool incrementalWorkloadAvailable;
    private static string incrementalWorkloadLogPath = "";
    private static long incrementalWorkloadLogOffset;
    private static string incrementalWorkloadPartialLine = "";
    private static FileSystemWatcher incrementalWorkloadWatcher;
    private static Timer incrementalWorkloadPollTimer;
    private static int incrementalWorkloadReadQueued;
    private static readonly Queue<string> incrementalRecentLogLines = new Queue<string>();
    private const int IncrementalRecentLogLineLimit = 5000;
    private static DateTime incrementalWorkloadLogWriteTimeUtc;
    private static readonly Queue<string> incrementalWorkloadHistoryLines = new Queue<string>();
    private const int IncrementalHistoryLineLimit = 20000;

    private static void InitializeIncrementalWorkloadTracker()
    {
        BootstrapIncrementalWorkloadTracker();
        try
        {
            var latest = GetLatestSaladLogFile();
            if (latest == null || latest.Directory == null)
            {
                return;
            }

            // FileSystemWatcher can be delayed while Salad holds its log open.
            // A one-second size probe reads no old content and bounds detection
            // latency without increasing Salad API or WSL traffic.
            incrementalWorkloadPollTimer = new Timer(
                delegate { QueueIncrementalWorkloadRead(); },
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));

            var watcher = new FileSystemWatcher(latest.Directory.FullName, "log-*.txt");
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
            FileSystemEventHandler changed = delegate { QueueIncrementalWorkloadRead(); };
            RenamedEventHandler renamed = delegate { QueueIncrementalWorkloadRead(); };
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += renamed;
            watcher.EnableRaisingEvents = true;
            incrementalWorkloadWatcher = watcher;
            Log("incremental_workload_tracker_started file=" + incrementalWorkloadLogPath);
        }
        catch (Exception ex)
        {
            Log("incremental_workload_tracker_start_error " + ex.Message);
        }
    }

    private static void StopIncrementalWorkloadTracker()
    {
        var pollTimer = incrementalWorkloadPollTimer;
        incrementalWorkloadPollTimer = null;
        if (pollTimer != null)
        {
            pollTimer.Dispose();
        }

        var watcher = incrementalWorkloadWatcher;
        incrementalWorkloadWatcher = null;
        if (watcher == null)
        {
            return;
        }

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            Log("incremental_workload_tracker_stop_error " + ex.Message);
        }
    }

    private static bool TryGetIncrementalWorkloadSnapshot(out WorkloadSnapshot snapshot)
    {
        lock (incrementalWorkloadLock)
        {
            snapshot = incrementalWorkloadSnapshot;
            return incrementalWorkloadAvailable;
        }
    }

    private static bool TryGetIncrementalLogSnapshot(out SaladLogSnapshot snapshot)
    {
        lock (incrementalWorkloadLock)
        {
            if (!incrementalWorkloadAvailable || string.IsNullOrEmpty(incrementalWorkloadLogPath))
            {
                snapshot = SaladLogSnapshot.Unavailable("Salad log missing");
                return false;
            }

            snapshot = new SaladLogSnapshot(
                true,
                "Salad log missing",
                incrementalWorkloadLogPath,
                incrementalWorkloadLogWriteTimeUtc,
                incrementalWorkloadLogOffset,
                incrementalRecentLogLines.ToArray());
            return true;
        }
    }

    private static bool TryDrainIncrementalWorkloadHistoryLines(out string sourceLog, out string[] lines)
    {
        lock (incrementalWorkloadLock)
        {
            sourceLog = incrementalWorkloadLogPath;
            if (incrementalWorkloadHistoryLines.Count == 0)
            {
                lines = new string[0];
                return false;
            }

            lines = incrementalWorkloadHistoryLines.ToArray();
            incrementalWorkloadHistoryLines.Clear();
            return true;
        }
    }

    private static void QueueIncrementalWorkloadRead()
    {
        if (Interlocked.CompareExchange(ref incrementalWorkloadReadQueued, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(delegate
        {
            var changed = false;
            try
            {
                Thread.Sleep(30);
                changed = ReadIncrementalWorkloadChanges();
            }
            catch (Exception ex)
            {
                Log("incremental_workload_read_error " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref incrementalWorkloadReadQueued, 0);
            }

            if (changed)
            {
                // Structural transitions and Pull progress both affect visible
                // status, so publish them without waiting for the main tick.
                PostToUi(SafeTick);
            }
        });
    }

    private static void PollIncrementalWorkloadTracker()
    {
        ReadIncrementalWorkloadChanges();
    }

    private static void BootstrapIncrementalWorkloadTracker()
    {
        var latest = GetLatestSaladLogFile();
        if (latest == null)
        {
            return;
        }

        var snapshot = ReadSaladLogSnapshot(latest);
        if (!snapshot.Available)
        {
            return;
        }

        var lines = snapshot.Lines;
        lock (incrementalWorkloadLock)
        {
            incrementalWorkloadSnapshot = new WorkloadSnapshot("", "No workloads", "incremental_bootstrap");
            incrementalWorkloadAvailable = true;
            incrementalRecentLogLines.Clear();
            incrementalWorkloadHistoryLines.Clear();
            // Recent state blocks contain the active instance plus lifecycle context; replaying the whole daily
            // log delays tray startup and grows without bound over long sessions.
            var firstLine = Math.Max(0, lines.Length - 5000);
            for (var i = firstLine; i < lines.Length; i++)
            {
                ApplyIncrementalWorkloadLine(lines[i]);
            }

            incrementalWorkloadLogPath = latest.FullName;
            incrementalWorkloadLogOffset = snapshot.Length;
            incrementalWorkloadLogWriteTimeUtc = latest.LastWriteTimeUtc;
            incrementalWorkloadPartialLine = "";
        }
    }

    private static bool ReadIncrementalWorkloadChanges()
    {
        var latest = GetLatestSaladLogFile();
        if (latest == null)
        {
            return false;
        }

        lock (incrementalWorkloadLock)
        {
            if (!string.Equals(incrementalWorkloadLogPath, latest.FullName, StringComparison.OrdinalIgnoreCase))
            {
                // Rotation and truncation require one replay; ordinary updates read appended bytes only.
                BootstrapIncrementalWorkloadTracker();
                return true;
            }

            byte[] bytes;
            using (var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                // The open stream reports appended bytes immediately even when
                // FileInfo.Length and LastWrite notifications are still stale.
                if (stream.Length < incrementalWorkloadLogOffset)
                {
                    BootstrapIncrementalWorkloadTracker();
                    return true;
                }

                if (stream.Length == incrementalWorkloadLogOffset)
                {
                    return false;
                }

                stream.Position = incrementalWorkloadLogOffset;
                bytes = new byte[stream.Length - stream.Position];
                var read = stream.Read(bytes, 0, bytes.Length);
                if (read != bytes.Length)
                {
                    Array.Resize(ref bytes, read);
                }

                incrementalWorkloadLogOffset = stream.Position;
            }
            incrementalWorkloadLogWriteTimeUtc = latest.LastWriteTimeUtc;

            var text = incrementalWorkloadPartialLine + Encoding.UTF8.GetString(bytes);
            var parts = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            incrementalWorkloadPartialLine = parts[parts.Length - 1];
            var changed = false;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                changed |= ApplyIncrementalWorkloadLine(parts[i]);
            }

            return changed;
        }
    }

    private static bool ApplyIncrementalWorkloadLine(string line)
    {
        incrementalRecentLogLines.Enqueue(line ?? "");
        while (incrementalRecentLogLines.Count > IncrementalRecentLogLineLimit)
        {
            incrementalRecentLogLines.Dequeue();
        }
        incrementalWorkloadHistoryLines.Enqueue(line ?? "");
        while (incrementalWorkloadHistoryLines.Count > IncrementalHistoryLineLimit)
        {
            incrementalWorkloadHistoryLines.Dequeue();
        }

        var before = incrementalWorkloadSnapshot;
        var received = Regex.Match(
            line ?? "",
            @"Workload Received:\s*\((?<id>[0-9a-fA-F]{8})\s*->.*?\((?<instance>[0-9a-fA-F]{8})\)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (received.Success)
        {
            SetIncrementalWorkload(
                received.Groups["id"].Value,
                received.Groups["instance"].Value,
                "Workload assigned",
                "incremental_received");
        }

        var desired = Regex.Match(
            line ?? "",
            @"CurrentWorkload\((?<id>[0-9a-fA-F]{8})\)\[(?<state>[^\]]+)\]",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (desired.Success)
        {
            var id = desired.Groups["id"].Value;
            var instance = string.Equals(incrementalWorkloadSnapshot.Id, id, StringComparison.OrdinalIgnoreCase)
                ? incrementalWorkloadSnapshot.InstanceId
                : "";
            SetIncrementalWorkload(id, instance, NormalizeIncrementalWorkloadState(desired.Groups["state"].Value), "incremental_desired");
        }
        else if ((line ?? "").IndexOf("Received desired state from matrix - 0 workloads", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            SetIncrementalWorkload("", "", "No workloads", "incremental_desired");
        }

        var state = Regex.Match(
            line ?? "",
            @"^\s*(?<id>[0-9a-fA-F]{8}):\s+\[(?<instance>[^\]]+)\]:(?<state>Installing|Pulling|Starting|Running|Stopped)\(",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (state.Success)
        {
            var id = state.Groups["id"].Value;
            var instance = NormalizeWorkloadInstanceId(state.Groups["instance"].Value);
            var rawState = state.Groups["state"].Value;
            if (string.Equals(rawState, "Stopped", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(incrementalWorkloadSnapshot.Id, id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(incrementalWorkloadSnapshot.InstanceId, instance, StringComparison.OrdinalIgnoreCase))
                {
                    SetIncrementalWorkload("", "", "No workloads", "incremental_stopped");
                }
            }
            else
            {
                SetIncrementalWorkload(id, instance, NormalizeIncrementalWorkloadState(rawState), "incremental_state");
            }
        }

        return !WorkloadSnapshotsEqual(before, incrementalWorkloadSnapshot) ||
            IsIncrementalPullProgressLine(line);
    }

    private static bool IsIncrementalPullProgressLine(string line)
    {
        var value = line ?? "";
        // Historical Prev[Pulling] dumps also contain Progress(...); only live
        // metrics events should wake the UI for percentage changes.
        return value.IndexOf("WLInstanceStatePulling(", StringComparison.OrdinalIgnoreCase) >= 0 &&
            value.IndexOf("Progress(", StringComparison.OrdinalIgnoreCase) >= 0 &&
            (value.IndexOf("SendStateChange(", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("GetMetrics(", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static void SetIncrementalWorkload(string id, string instanceId, string state, string source)
    {
        incrementalWorkloadSnapshot = new WorkloadSnapshot(id, state, source, instanceId);
        incrementalWorkloadAvailable = true;
    }

    private static string NormalizeIncrementalWorkloadState(string state)
    {
        var value = (state ?? "").Trim();
        if (string.Equals(value, "Running", StringComparison.OrdinalIgnoreCase))
        {
            return "Chopping";
        }

        if (string.Equals(value, "Installing", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Pulling", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Starting", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Disconnected", StringComparison.OrdinalIgnoreCase))
        {
            return "Workload assigned";
        }

        return NormalizeWorkloadState(value);
    }

    private static bool WorkloadSnapshotsEqual(WorkloadSnapshot left, WorkloadSnapshot right)
    {
        return string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.InstanceId, right.InstanceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.State, right.State, StringComparison.OrdinalIgnoreCase);
    }
}
