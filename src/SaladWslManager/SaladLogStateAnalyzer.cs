using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

internal static partial class Program
{
    // Project one shared Salad log snapshot into workload, pull-health, and timing state.
    private static bool IsGpuWorkloadState(string workloadState)
    {
        return string.Equals(workloadState, "Chopping", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(workloadState, "Workload assigned", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNoWorkloadsState(string workloadState)
    {
        return string.Equals(workloadState, "No workloads", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeToQuitSaladApp(string workloadState, string distroState)
    {
        if (IsGpuWorkloadState(workloadState))
        {
            return false;
        }

        if (IsNoWorkloadsState(workloadState) ||
            string.Equals(workloadState, "Paused", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(distroState, "STOPPED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(distroState, "NOT_FOUND", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRecentSaladWorkloadState()
    {
        return GetRecentSaladWorkloadSnapshot().State;
    }

    private static WorkloadSnapshot GetRecentSaladWorkloadSnapshot()
    {
        return GetRecentSaladWorkloadSnapshot(GetRecentSaladLogSnapshot());
    }

    private static WorkloadSnapshot GetRecentSaladWorkloadSnapshot(SaladLogSnapshot snapshot)
    {
        WorkloadSnapshot incremental;
        if (TryGetIncrementalWorkloadSnapshot(out incremental))
        {
            return incremental;
        }

        try
        {
            if (!snapshot.Available)
            {
                return new WorkloadSnapshot("", snapshot.UnavailableState, "");
            }

            var lines = snapshot.Lines;
            var latestStateHeaderIndex = FindLatestLogLineIndex(lines, "Workload Instance States:");
            var latestDesiredStateIndex = FindLatestLogLineIndex(lines, "Received desired state from matrix");
            if (latestStateHeaderIndex >= 0 && latestStateHeaderIndex >= latestDesiredStateIndex)
            {
                // Prefer the newer state block so an older desired-state event
                // cannot hide a workload that has already started or stopped.
                return GetLatestWorkloadStateBlockSnapshot(lines, latestStateHeaderIndex);
            }

            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (line.IndexOf("Received desired state from matrix", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var current = Regex.Match(
                    line,
                    @"CurrentWorkload\((?<id>[0-9a-fA-F]{8})\)\[(?<state>[^\]]+)\]",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                if (current.Success)
                {
                    // A later Running State:false is newer than the desired-state
                    // record and means the workload is no longer active.
                    if (HasLaterRunningStateFalse(lines, i))
                    {
                        return new WorkloadSnapshot("", "No workloads", "running_state");
                    }

                    return new WorkloadSnapshot(
                        current.Groups["id"].Value,
                        NormalizeWorkloadState(current.Groups["state"].Value),
                        "CurrentWorkload",
                        FindLatestWorkloadInstanceId(lines, current.Groups["id"].Value));
                }

                if (line.IndexOf("0 workloads", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new WorkloadSnapshot("", "No workloads", "desired_state");
                }

                break;
            }

            WorkloadSnapshot latestPulling = new WorkloadSnapshot("", "", "");
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var row = lines[i];
                var stateMatch = Regex.Match(
                    row,
                    @"^\s*(?<id>[0-9a-fA-F]{8}):\s+\[[^\]]+\]:(?<state>Running|Pulling|Starting|Stopped)\(",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                if (stateMatch.Success)
                {
                    var id = stateMatch.Groups["id"].Value;
                    var instanceId = GetWorkloadInstanceId(row);
                    var rawState = stateMatch.Groups["state"].Value;
                    var normalizedState = NormalizeWorkloadState(rawState);
                    if (string.Equals(rawState, "Running", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(rawState, "Starting", StringComparison.OrdinalIgnoreCase))
                    {
                        return new WorkloadSnapshot(id, normalizedState, rawState, instanceId);
                    }

                    if (latestPulling.Id.Length == 0 &&
                        string.Equals(rawState, "Pulling", StringComparison.OrdinalIgnoreCase))
                    {
                        latestPulling = new WorkloadSnapshot(id, normalizedState, rawState, instanceId);
                    }
                }
            }

            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (line.IndexOf("Received desired state from matrix", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var current = Regex.Match(
                        line,
                        @"CurrentWorkload\((?<id>[0-9a-fA-F]{8})\)\[(?<state>[^\]]+)\]",
                        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                    if (current.Success)
                    {
                        return new WorkloadSnapshot(
                            current.Groups["id"].Value,
                            NormalizeWorkloadState(current.Groups["state"].Value),
                            "CurrentWorkload",
                            FindLatestWorkloadInstanceId(lines, current.Groups["id"].Value));
                    }

                    if (line.IndexOf("0 workloads", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return new WorkloadSnapshot("", "No workloads", "desired_state");
                    }

                    if (line.IndexOf("[Running]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        line.IndexOf("CurrentWorkload", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        line.IndexOf("[Running", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return new WorkloadSnapshot("", "Chopping", "desired_state");
                    }

                    if (latestPulling.Id.Length > 0)
                    {
                        return latestPulling;
                    }

                    return new WorkloadSnapshot("", "Workload assigned", "desired_state");
                }

                if (line.IndexOf("Running State: true", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new WorkloadSnapshot("", "Chopping", "running_state");
                }

                if (line.IndexOf("Running State: false", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new WorkloadSnapshot("", "Paused", "running_state");
                }
            }
        }
        catch (Exception ex)
        {
            Log("salad_workload_probe_error " + ex.Message);
        }

        return new WorkloadSnapshot("", "Workload unknown", "");
    }

    private static int FindLatestLogLineIndex(string[] lines, string marker)
    {
        if (lines == null || string.IsNullOrEmpty(marker))
        {
            return -1;
        }

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static WorkloadSnapshot GetLatestWorkloadStateBlockSnapshot(string[] lines, int headerIndex)
    {
        WorkloadSnapshot latestAssigned = new WorkloadSnapshot("", "", "");
        if (headerIndex < 0)
        {
            return new WorkloadSnapshot("", "Workload unknown", "");
        }

        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            var stateMatch = Regex.Match(
                lines[i],
                @"^\s*(?<id>[0-9a-fA-F]{8}):\s+\[(?<instance>[^\]]+)\]:(?<state>Running|Pulling|Starting|Stopped)\(",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (!stateMatch.Success)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                break;
            }

            var id = stateMatch.Groups["id"].Value;
            var instanceId = NormalizeWorkloadInstanceId(stateMatch.Groups["instance"].Value);
            var rawState = stateMatch.Groups["state"].Value;
            if (string.Equals(rawState, "Running", StringComparison.OrdinalIgnoreCase))
            {
                return new WorkloadSnapshot(id, "Chopping", "workload_states", instanceId);
            }

            if (string.Equals(rawState, "Pulling", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rawState, "Starting", StringComparison.OrdinalIgnoreCase))
            {
                latestAssigned = new WorkloadSnapshot(id, NormalizeWorkloadState(rawState), "workload_states", instanceId);
            }
        }

        return latestAssigned.Id.Length > 0
            ? latestAssigned
            : new WorkloadSnapshot("", "No workloads", "workload_states");
    }

    private static bool HasLaterRunningStateFalse(string[] lines, int index)
    {
        for (var i = index + 1; i < lines.Length; i++)
        {
            if (lines[i].IndexOf("Running State: false", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (lines[i].IndexOf("Received desired state from matrix", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
        }

        return false;
    }

    private static string NormalizeWorkloadState(string state)
    {
        if (string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
        {
            return "Chopping";
        }

        if (string.Equals(state, "Pulling", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "Starting", StringComparison.OrdinalIgnoreCase))
        {
            return "Workload assigned";
        }

        if (string.Equals(state, "Stopped", StringComparison.OrdinalIgnoreCase))
        {
            return "Paused";
        }

        return string.IsNullOrEmpty(state) ? "Workload unknown" : state;
    }

    private static string GetRecentPullHealthStatus()
    {
        return GetRecentPullHealthStatus(GetRecentSaladLogSnapshot());
    }

    private static string GetRecentPullHealthStatus(SaladLogSnapshot snapshot)
    {
        try
        {
            if (!snapshot.Available)
            {
                return "Pull: ?";
            }

            var lines = snapshot.Lines;

            var states = new System.Collections.Generic.Dictionary<string, PullWorkloadState>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Salad can omit the progress suffix while a pull is active;
                // keep that row visible instead of treating it as history.
                var pulling = Regex.Match(
                    line,
                    @"^\s*(?<id>[0-9a-fA-F]{8}):\s+\[[^\]]+\]:Pulling\((?<duration>[^)]*)\)(?:\s+-\s+(?<percent>\d+)%\s*)?",
                    RegexOptions.CultureInvariant);
                if (pulling.Success)
                {
                    var id = pulling.Groups["id"].Value;
                    PullWorkloadState previous;
                    if (!states.TryGetValue(id, out previous))
                    {
                        previous = CreatePullWorkloadState();
                    }

                    if (previous.Seen && !previous.Active)
                    {
                        previous = CreatePullWorkloadState();
                    }

                    previous.Seen = true;
                    previous.Active = true;
                    // State rows often omit their percentage. Preserve progress
                    // observed from the more frequent Progress(...) events.
                    if (pulling.Groups["percent"].Success)
                    {
                        ApplyPullProgress(
                            ref previous,
                            int.Parse(pulling.Groups["percent"].Value, CultureInfo.InvariantCulture));
                    }

                    previous.DurationText = pulling.Groups["duration"].Value;
                    previous.Duration = ParseSaladDuration(previous.DurationText);
                    states[id] = previous;
                    continue;
                }

                var progressEvent = Regex.Match(
                    line,
                    @"(?:GetMetrics|SendStateChange)\(SourceId \{.*?Name = Pulling - WLInstanceStatePulling\((?<id>[0-9a-fA-F]{8})\s+-\s+(?<duration>[^)]*)\),\s*Progress\((?<progress>[0-9]+(?:\.[0-9]+)?)\)",
                    RegexOptions.CultureInvariant);
                if (progressEvent.Success)
                {
                    var id = progressEvent.Groups["id"].Value;
                    double progress;
                    if (!double.TryParse(
                        progressEvent.Groups["progress"].Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out progress))
                    {
                        continue;
                    }

                    PullWorkloadState previous;
                    if (!states.TryGetValue(id, out previous) || !previous.Active)
                    {
                        previous = CreatePullWorkloadState();
                    }

                    // Salad emits normalized progress every few seconds. Reading
                    // this existing log event keeps monitoring read-only while
                    // avoiding dependence on the infrequent summary block.
                    previous.Seen = true;
                    previous.Active = true;
                    ApplyPullProgress(
                        ref previous,
                        (int)Math.Round(Math.Max(0.0, Math.Min(1.0, progress)) * 100.0, MidpointRounding.AwayFromZero));
                    previous.DurationText = progressEvent.Groups["duration"].Value;
                    previous.Duration = ParseSaladDuration(previous.DurationText);
                    states[id] = previous;
                    continue;
                }

                var notPulling = Regex.Match(
                    line,
                    @"^\s*(?<id>[0-9a-fA-F]{8}):\s+\[[^\]]+\]:(?:Running|Starting|Stopped)\(",
                    RegexOptions.CultureInvariant);
                if (notPulling.Success)
                {
                    var id = notPulling.Groups["id"].Value;
                    PullWorkloadState previous;
                    if (!states.TryGetValue(id, out previous))
                    {
                        previous = CreatePullWorkloadState();
                    }

                    previous.Active = false;
                    states[id] = previous;
                }
            }

            var latestStateHeaderIndex = -1;
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].IndexOf("Workload Instance States:", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    latestStateHeaderIndex = i;
                    break;
                }
            }

            if (latestStateHeaderIndex < 0)
            {
                return "Pull: ?";
            }

            // The newest state block is the current state. Older rows remain
            // useful only for rollback history and must not keep Pull at OK.
            var latestActivePullIds = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var latestHasRunningWorkload = false;
            for (var i = latestStateHeaderIndex + 1; i < lines.Length; i++)
            {
                var row = lines[i];
                var pulling = Regex.Match(
                    row,
                    @"^\s*(?<id>[0-9a-fA-F]{8}):\s+\[[^\]]+\]:Pulling\((?<duration>[^)]*)\)(?:\s+-\s+(?<percent>\d+)%\s*)?",
                    RegexOptions.CultureInvariant);
                if (pulling.Success)
                {
                    latestActivePullIds.Add(pulling.Groups["id"].Value);
                    continue;
                }

                var currentState = Regex.Match(
                    row,
                    @"^\s*(?<id>[0-9a-fA-F]{8}):\s+\[[^\]]+\]:(?<state>Running|Starting|Stopped)\(",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                if (currentState.Success)
                {
                    if (string.Equals(currentState.Groups["state"].Value, "Running", StringComparison.OrdinalIgnoreCase))
                    {
                        latestHasRunningWorkload = true;
                    }

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(row))
                {
                    break;
                }
            }

            PullWorkloadState worstRollback = new PullWorkloadState();
            string worstRollbackId = null;
            PullWorkloadState worstStuck = new PullWorkloadState();
            string worstStuckId = null;
            foreach (var pair in states)
            {
                var state = pair.Value;
                if (!state.Active || !latestActivePullIds.Contains(pair.Key))
                {
                    continue;
                }

                if (state.RollbackCount > 0 &&
                    (worstRollbackId == null || state.RollbackCount > worstRollback.RollbackCount))
                {
                    worstRollbackId = pair.Key;
                    worstRollback = state;
                }

                if (state.Percent < 100 &&
                    state.Duration >= TimeSpan.FromMinutes(45) &&
                    (worstStuckId == null || state.Duration > worstStuck.Duration))
                {
                    worstStuckId = pair.Key;
                    worstStuck = state;
                }
            }

            if (worstRollbackId != null)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Pull: rollback {0} {1}->{2}%",
                    worstRollbackId,
                    worstRollback.LastRollbackFrom,
                    worstRollback.LastRollbackTo);
            }

            if (worstStuckId != null)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Pull: stuck {0} {1} {2}",
                    worstStuckId,
                    worstStuck.DurationText,
                    worstStuck.Percent >= 0 ? worstStuck.Percent.ToString(CultureInfo.InvariantCulture) + "%" : "?");
            }

            if (latestActivePullIds.Count > 0)
            {
                string currentPullId = null;
                PullWorkloadState currentPull = new PullWorkloadState();
                foreach (var id in latestActivePullIds)
                {
                    PullWorkloadState state;
                    if (!states.TryGetValue(id, out state))
                    {
                        continue;
                    }

                    if (currentPullId == null || state.Percent < currentPull.Percent)
                    {
                        currentPullId = id;
                        currentPull = state;
                    }
                }

                if (currentPullId != null)
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "Pull: pulling ( {0} )",
                        currentPull.Percent >= 0 ? currentPull.Percent.ToString(CultureInfo.InvariantCulture) + "%" : "--%");
                }
            }

            return latestHasRunningWorkload ? "Pull: OK" : "Pull: idle";
        }
        catch (Exception ex)
        {
            Log("pull_health_probe_error " + ex.Message);
            return "Pull: ?";
        }
    }

    private static PullWorkloadState CreatePullWorkloadState()
    {
        // Unknown must remain distinct from 0%, which is a valid initial pull
        // progress value emitted by Salad.
        return new PullWorkloadState { Percent = -1 };
    }

    private static void ApplyPullProgress(ref PullWorkloadState state, int percent)
    {
        var normalized = Math.Max(0, Math.Min(100, percent));
        if (state.Seen && state.Percent >= 0 && normalized + 20 < state.Percent)
        {
            state.RollbackCount++;
            state.LastRollbackFrom = state.Percent;
            state.LastRollbackTo = normalized;
        }

        state.Percent = normalized;
    }

    private static TimeSpan ParseSaladDuration(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.Zero;
        }

        var days = 0;
        var hours = 0;
        var minutes = 0;
        var seconds = 0;
        var matches = Regex.Matches(value, @"(?<n>\d+)(?<u>d|h|m|s)", RegexOptions.CultureInvariant);
        foreach (Match match in matches)
        {
            var n = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            switch (match.Groups["u"].Value)
            {
                case "d":
                    days = n;
                    break;
                case "h":
                    hours = n;
                    break;
                case "m":
                    minutes = n;
                    break;
                case "s":
                    seconds = n;
                    break;
            }
        }

        return new TimeSpan(days, hours, minutes, seconds);
    }

    private static WorkloadTimingSnapshot GetRecentWorkloadTimingStatus(WorkloadSnapshot workload)
    {
        return GetRecentWorkloadTimingStatus(GetRecentSaladLogSnapshot(), workload);
    }

    private static WorkloadTimingSnapshot GetRecentWorkloadTimingStatus(SaladLogSnapshot snapshot, WorkloadSnapshot workload)
    {
        try
        {
            if (!snapshot.Available)
            {
                return new WorkloadTimingSnapshot("?", "?");
            }

            var lines = snapshot.Lines;

            // Runtime is for the current workload only. Historic Running rows
            // must not reappear after the workload has stopped or been paused.
            var runtime = IsGpuWorkloadState(workload.State) && !string.IsNullOrEmpty(workload.Id)
                ? GetCurrentWorkloadRuntime(lines, workload.Id)
                : TimeSpan.Zero;
            var pastAverage = GetPastCompletedWorkloadAverage(lines, workload.Id);
            return new WorkloadTimingSnapshot(FormatWorkloadDuration(runtime), FormatWorkloadDuration(pastAverage));
        }
        catch (Exception ex)
        {
            Log("workload_timing_probe_error " + ex.Message);
            return new WorkloadTimingSnapshot("?", "?");
        }
    }

    private static TimeSpan GetCurrentWorkloadRuntime(string[] lines, string workloadId)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            var pattern = string.IsNullOrEmpty(workloadId)
                ? @"^\s*(?<id>[0-9a-fA-F]{8}):\s+\[[^\]]+\]:(?:Running|Starting|Pulling)\((?<inlineDuration>[^)]*)\)(?:.*?\sfor\s(?<forDuration>[0-9dhms:]+))?"
                : @"^\s*" + Regex.Escape(workloadId) + @":\s+\[[^\]]+\]:(?:Running|Starting|Pulling)\((?<inlineDuration>[^)]*)\)(?:.*?\sfor\s(?<forDuration>[0-9dhms:]+))?";
            var match = Regex.Match(line, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            var durationText = match.Groups["forDuration"].Success
                ? match.Groups["forDuration"].Value
                : match.Groups["inlineDuration"].Value;
            if (line.IndexOf(":Running(", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var startedAtMatch = Regex.Match(
                    line,
                    @"StartedAt\s+(?<at>[0-9T:\-]+)",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                if (startedAtMatch.Success)
                {
                    var startedAt = ParseSaladUtcTimestamp(startedAtMatch.Groups["at"].Value);
                    if (startedAt.HasValue && startedAt.Value <= DateTimeOffset.Now)
                    {
                        return DateTimeOffset.Now - startedAt.Value;
                    }
                }
            }

            var duration = ParseSaladDuration(durationText);
            if (duration > TimeSpan.Zero)
            {
                return duration;
            }
        }

        return TimeSpan.Zero;
    }

    private static TimeSpan GetPastCompletedWorkloadAverage(string[] lines, string currentWorkloadId)
    {
        var persistedAverage = GetPersistedPastCompletedWorkloadAverage(currentWorkloadId);
        if (persistedAverage > TimeSpan.Zero)
        {
            return persistedAverage;
        }

        return GetLogPastCompletedWorkloadAverage(lines, currentWorkloadId);
    }

    private static string FormatWorkloadDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return "?";
        }

        if (value.TotalDays >= 1)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}d{1}h", (int)value.TotalDays, value.Hours);
        }

        if (value.TotalHours >= 1)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}h{1}m", (int)value.TotalHours, value.Minutes);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0}m{1}s", value.Minutes, value.Seconds);
    }

    private struct WorkloadSnapshot
    {
        public readonly string Id;
        public readonly string InstanceId;
        public readonly string State;
        public readonly string Source;

        public WorkloadSnapshot(string id, string state, string source, string instanceId = "")
        {
            Id = id ?? "";
            InstanceId = NormalizeWorkloadInstanceId(instanceId);
            State = state ?? "Workload unknown";
            Source = source ?? "";
        }

        public string DisplayId
        {
            get
            {
                if (string.IsNullOrEmpty(Id))
                {
                    return "";
                }

                // The full pair remains available for lifecycle checks; only the UI is abbreviated.
                return string.IsNullOrEmpty(InstanceId)
                    ? Id
                    : Id.Substring(0, Math.Min(4, Id.Length)) + "-" +
                        InstanceId.Substring(0, Math.Min(4, InstanceId.Length));
            }
        }
    }

    private static string GetWorkloadInstanceId(string stateLine)
    {
        var match = Regex.Match(stateLine ?? "", @"^\s*[0-9a-fA-F]{8}:\s+\[(?<instance>[^\]]+)\]:");
        return match.Success ? NormalizeWorkloadInstanceId(match.Groups["instance"].Value) : "";
    }

    private static string FindLatestWorkloadInstanceId(string[] lines, string workloadId)
    {
        if (lines == null || string.IsNullOrEmpty(workloadId))
        {
            return "";
        }

        // Desired-state records omit the instance ID, so enrich them from the newest matching state row.
        var pattern = @"^\s*" + Regex.Escape(workloadId) + @":\s+\[(?<instance>[^\]]+)\]:";
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var match = Regex.Match(lines[i], pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return NormalizeWorkloadInstanceId(match.Groups["instance"].Value);
            }
        }

        return "";
    }

    private static string NormalizeWorkloadInstanceId(string instanceId)
    {
        var value = (instanceId ?? "").Trim();
        return string.Equals(value, "NCW", StringComparison.OrdinalIgnoreCase) ? "" : value;
    }

    private struct WorkloadTimingSnapshot
    {
        public readonly string RuntimeText;
        public readonly string PastAverageText;

        public WorkloadTimingSnapshot(string runtimeText, string pastAverageText)
        {
            RuntimeText = runtimeText ?? "?";
            PastAverageText = pastAverageText ?? "?";
        }
    }




    private struct PullWorkloadState
    {
        public bool Seen;
        public bool Active;
        public int Percent;
        public int RollbackCount;
        public int LastRollbackFrom;
        public int LastRollbackTo;
        public string DurationText;
        public TimeSpan Duration;
    }
}
