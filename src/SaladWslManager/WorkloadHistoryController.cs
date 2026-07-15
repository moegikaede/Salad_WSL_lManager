using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal static partial class Program
{
    private static readonly TimeSpan WorkloadRewardAttributionGrace = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan WorkloadHistoryRefreshInterval = TimeSpan.FromSeconds(30);
    private const int WorkloadHistoryLogFileLimit = 12;
    private static readonly System.Collections.Generic.Dictionary<string, long> processedWorkloadHistoryLogLengths =
        new System.Collections.Generic.Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    private static void QueueWorkloadHistoryRefreshIfNeeded(SaladLogSnapshot currentSnapshot)
    {
        var now = DateTimeOffset.Now;
        if (now - lastWorkloadHistoryRefresh < WorkloadHistoryRefreshInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref workloadHistoryRefreshRunning, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                lastWorkloadHistoryRefresh = DateTimeOffset.Now;
                RefreshWorkloadHistory(currentSnapshot);
            }
            catch (Exception ex)
            {
                Log("workload_history_refresh_error " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref workloadHistoryRefreshRunning, 0);
            }
        });
    }

    private static void RefreshWorkloadHistory(SaladLogSnapshot currentSnapshot)
    {
        var rows = ReadWorkloadHistoryRows();
        var changed = false;

        string incrementalSource;
        string[] incrementalLines;
        if (TryDrainIncrementalWorkloadHistoryLines(out incrementalSource, out incrementalLines))
        {
            changed |= MergeWorkloadHistoryFromLines(rows, incrementalLines, incrementalSource);
        }

        foreach (var logFile in GetRecentSaladLogFiles(WorkloadHistoryLogFileLimit))
        {
            if (currentSnapshot.Available &&
                string.Equals(currentSnapshot.FilePath, logFile.FullName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long processedLength;
            if (processedWorkloadHistoryLogLengths.TryGetValue(logFile.FullName, out processedLength) &&
                processedLength == logFile.Length)
            {
                continue;
            }

            string[] lines;
            if (!TryReadAllLinesShared(logFile.FullName, out lines))
            {
                continue;
            }

            changed |= MergeWorkloadHistoryFromLines(rows, lines, logFile.FullName);
            processedWorkloadHistoryLogLengths[logFile.FullName] = logFile.Length;
        }

        changed |= MergeWorkloadRewards(rows);
        if (changed)
        {
            WriteWorkloadHistoryRows(rows);
        }
    }

    private static FileInfo[] GetRecentSaladLogFiles(int limit)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Salad",
            "logs");
        if (!Directory.Exists(logDir))
        {
            return new FileInfo[0];
        }

        return new DirectoryInfo(logDir)
            .GetFiles("log-*.txt")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(limit)
            .ToArray();
    }

    private static bool TryReadAllLinesShared(string path, out string[] lines)
    {
        lines = new string[0];
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                lines = reader.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log("workload_history_log_read_error path=" + path + " " + ex.Message);
            return false;
        }
    }

    private static bool MergeWorkloadHistoryFromLines(
        System.Collections.Generic.Dictionary<string, WorkloadHistoryRow> rows,
        string[] lines,
        string sourceLog)
    {
        var changed = false;
        foreach (var line in lines)
        {
            WorkloadHistoryRow parsed;
            if (!TryParseWorkloadHistoryLine(line, sourceLog, out parsed))
            {
                continue;
            }

            WorkloadHistoryRow existing;
            if (!rows.TryGetValue(parsed.Key, out existing))
            {
                rows[parsed.Key] = parsed;
                changed = true;
                continue;
            }

            if (MergeWorkloadHistoryRow(ref existing, parsed))
            {
                rows[parsed.Key] = existing;
                changed = true;
            }
        }

        return changed;
    }

    private static bool TryParseWorkloadHistoryLine(string line, string sourceLog, out WorkloadHistoryRow row)
    {
        row = new WorkloadHistoryRow();
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = Regex.Match(
            line,
            @"^\s*(?<id>[0-9a-fA-F]{8}):\s+\[(?<instance>[^\]]+)\]:(?<state>Running|Pulling|Starting|Stopped)\((?<detail>[^)]*)\)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        var workloadId = match.Groups["id"].Value;
        var instanceId = match.Groups["instance"].Value;
        if (!IsRealWorkloadInstance(instanceId))
        {
            return false;
        }

        var state = match.Groups["state"].Value;
        var timing = Regex.Match(
            line,
            @"(?<timeKind>StartedAt|RanAt)\s+(?<at>[0-9T:\-]+)\s+for\s+(?<duration>[0-9dhms:]+)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var duration = timing.Success ? ParseSaladDuration(timing.Groups["duration"].Value) : TimeSpan.Zero;
        var at = timing.Success ? ParseSaladUtcTimestamp(timing.Groups["at"].Value) : null;
        DateTimeOffset? startedAt = null;
        DateTimeOffset? stoppedAt = null;
        if (at.HasValue)
        {
            if (string.Equals(timing.Groups["timeKind"].Value, "StartedAt", StringComparison.OrdinalIgnoreCase))
            {
                startedAt = at.Value;
            }
            else if (string.Equals(timing.Groups["timeKind"].Value, "RanAt", StringComparison.OrdinalIgnoreCase))
            {
                startedAt = at.Value;
                if (duration > TimeSpan.Zero)
                {
                    stoppedAt = at.Value + duration;
                }
            }
        }

        row = new WorkloadHistoryRow
        {
            WorkloadId = workloadId,
            InstanceId = instanceId,
            LastState = state,
            StartedAtLocal = startedAt,
            StoppedAtLocal = stoppedAt,
            RuntimeSeconds = duration > TimeSpan.Zero ? (double?)duration.TotalSeconds : null,
            LastSeenAtLocal = TryParseSaladLogPrefixLocalTime(line),
            SourceLog = Path.GetFileName(sourceLog) ?? sourceLog
        };
        return true;
    }

    private static bool IsRealWorkloadInstance(string instanceId)
    {
        return !string.IsNullOrWhiteSpace(instanceId) &&
            !string.Equals(instanceId.Trim(), "NCW", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? ParseSaladUtcTimestamp(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        DateTime parsed;
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc)).ToLocalTime();
    }

    private static DateTimeOffset? TryParseSaladLogPrefixLocalTime(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var match = Regex.Match(
            line,
            @"^(?<timestamp>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d+\s+[+\-]\d{2}:\d{2})",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        DateTimeOffset value;
        return DateTimeOffset.TryParse(match.Groups["timestamp"].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            ? (DateTimeOffset?)value
            : null;
    }

    private static bool MergeWorkloadHistoryRow(ref WorkloadHistoryRow existing, WorkloadHistoryRow parsed)
    {
        var changed = false;
        if (!string.IsNullOrEmpty(parsed.LastState) &&
            (IsLater(parsed.LastSeenAtLocal, existing.LastSeenAtLocal) ||
                string.IsNullOrEmpty(existing.LastState)))
        {
            changed |= SetIfChanged(ref existing.LastState, parsed.LastState);
        }

        if (!existing.StartedAtLocal.HasValue ||
            parsed.StartedAtLocal.HasValue && parsed.StartedAtLocal.Value < existing.StartedAtLocal.Value)
        {
            existing.StartedAtLocal = parsed.StartedAtLocal ?? existing.StartedAtLocal;
            changed = true;
        }

        if (!existing.StoppedAtLocal.HasValue ||
            parsed.StoppedAtLocal.HasValue && parsed.StoppedAtLocal.Value > existing.StoppedAtLocal.Value)
        {
            existing.StoppedAtLocal = parsed.StoppedAtLocal ?? existing.StoppedAtLocal;
            changed = true;
        }

        if (parsed.StoppedAtLocal.HasValue &&
            !string.Equals(existing.LastState, parsed.LastState, StringComparison.OrdinalIgnoreCase))
        {
            existing.LastState = parsed.LastState;
            changed = true;
        }

        if (!existing.RuntimeSeconds.HasValue ||
            parsed.RuntimeSeconds.HasValue && parsed.RuntimeSeconds.Value > existing.RuntimeSeconds.Value)
        {
            existing.RuntimeSeconds = parsed.RuntimeSeconds ?? existing.RuntimeSeconds;
            changed = true;
        }

        if (IsLater(parsed.LastSeenAtLocal, existing.LastSeenAtLocal))
        {
            existing.LastSeenAtLocal = parsed.LastSeenAtLocal;
            changed = true;
        }

        changed |= SetIfChanged(ref existing.SourceLog, parsed.SourceLog);
        return changed;
    }

    private static bool IsLater(DateTimeOffset? value, DateTimeOffset? current)
    {
        return value.HasValue && (!current.HasValue || value.Value > current.Value);
    }

    private static bool SetIfChanged(ref string target, string value)
    {
        value = value ?? "";
        if (string.Equals(target ?? "", value, StringComparison.Ordinal))
        {
            return false;
        }

        target = value;
        return true;
    }

    private static System.Collections.Generic.Dictionary<string, WorkloadHistoryRow> ReadWorkloadHistoryRows()
    {
        var rows = new System.Collections.Generic.Dictionary<string, WorkloadHistoryRow>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(WorkloadHistoryCsvPath))
        {
            return rows;
        }

        try
        {
            using (var stream = new FileStream(WorkloadHistoryCsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                var headerLine = reader.ReadLine();
                if (headerLine == null)
                {
                    return rows;
                }

                var headers = ParseCsvLine(headerLine);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var fields = ParseCsvLine(line);
                    var row = WorkloadHistoryRow.FromCsv(headers, fields);
                    if (!string.IsNullOrEmpty(row.Key))
                    {
                        rows[row.Key] = row;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("workload_history_read_error " + ex.Message);
        }

        return rows;
    }

    private static void WriteWorkloadHistoryRows(System.Collections.Generic.Dictionary<string, WorkloadHistoryRow> rows)
    {
        Directory.CreateDirectory(AppDir);
        var tempPath = WorkloadHistoryCsvPath + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.WriteLine("workload_id,instance_id,last_state,started_at_local,stopped_at_local,runtime_seconds,runtime_text,estimated_reward_usd,balance_delta_usd,observation_count,last_seen_at_local,source_log");
            foreach (var row in rows.Values
                .OrderByDescending(r => r.LastSeenAtLocal ?? DateTimeOffset.MinValue)
                .ThenBy(r => r.WorkloadId, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteLine(row.ToCsv());
            }
        }

        if (File.Exists(WorkloadHistoryCsvPath))
        {
            File.Delete(WorkloadHistoryCsvPath);
        }

        File.Move(tempPath, WorkloadHistoryCsvPath);
    }

    private static TimeSpan GetPersistedPastCompletedWorkloadAverage(string currentWorkloadId)
    {
        var durations = ReadWorkloadHistoryRows()
            .Values
            .Where(row => row.RuntimeSeconds.HasValue && row.RuntimeSeconds.Value > 0)
            .Where(row => row.StoppedAtLocal.HasValue)
            .Where(row => string.IsNullOrEmpty(currentWorkloadId) ||
                !string.Equals(row.WorkloadId, currentWorkloadId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.StoppedAtLocal.Value)
            .Take(50)
            .Select(row => row.RuntimeSeconds.Value)
            .ToArray();

        return durations.Length == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(durations.Average());
    }

    private static TimeSpan GetLogPastCompletedWorkloadAverage(string[] lines, string currentWorkloadId)
    {
        var durations = new System.Collections.Generic.List<TimeSpan>();
        for (var i = lines.Length - 1; i >= 0 && durations.Count < 50; i--)
        {
            WorkloadHistoryRow row;
            if (!TryParseWorkloadHistoryLine(lines[i], "", out row) ||
                !row.StoppedAtLocal.HasValue ||
                !row.RuntimeSeconds.HasValue)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(currentWorkloadId) &&
                string.Equals(row.WorkloadId, currentWorkloadId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            durations.Add(TimeSpan.FromSeconds(row.RuntimeSeconds.Value));
        }

        return durations.Count == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(durations.Average(d => d.TotalSeconds));
    }

    private struct WorkloadHistoryRow
    {
        public string WorkloadId;
        public string InstanceId;
        public string LastState;
        public DateTimeOffset? StartedAtLocal;
        public DateTimeOffset? StoppedAtLocal;
        public double? RuntimeSeconds;
        public double? EstimatedRewardUsd;
        public double? BalanceDeltaUsd;
        public int ObservationCount;
        public DateTimeOffset? LastSeenAtLocal;
        public string SourceLog;

        public string Key
        {
            get { return string.IsNullOrEmpty(WorkloadId) || string.IsNullOrEmpty(InstanceId) ? "" : WorkloadId + "|" + InstanceId; }
        }

        public string ToCsv()
        {
            return Csv(WorkloadId) + "," +
                Csv(InstanceId) + "," +
                Csv(LastState) + "," +
                Csv(FormatDateTimeOffset(StartedAtLocal)) + "," +
                Csv(FormatDateTimeOffset(StoppedAtLocal)) + "," +
                FormatNullable(RuntimeSeconds) + "," +
                Csv(RuntimeSeconds.HasValue ? FormatWorkloadDuration(TimeSpan.FromSeconds(RuntimeSeconds.Value)) : "") + "," +
                FormatNullable(EstimatedRewardUsd) + "," +
                FormatNullable(BalanceDeltaUsd) + "," +
                ObservationCount.ToString(CultureInfo.InvariantCulture) + "," +
                Csv(FormatDateTimeOffset(LastSeenAtLocal)) + "," +
                Csv(SourceLog);
        }

        public static WorkloadHistoryRow FromCsv(string[] headers, string[] fields)
        {
            var row = new WorkloadHistoryRow();
            row.WorkloadId = GetCsvField(headers, fields, "workload_id");
            row.InstanceId = GetCsvField(headers, fields, "instance_id");
            row.LastState = GetCsvField(headers, fields, "last_state");
            row.StartedAtLocal = ParseCsvDateTimeOffset(GetCsvField(headers, fields, "started_at_local"));
            row.StoppedAtLocal = ParseCsvDateTimeOffset(GetCsvField(headers, fields, "stopped_at_local"));
            row.RuntimeSeconds = ParseCsvNullableDouble(GetCsvField(headers, fields, "runtime_seconds"));
            row.EstimatedRewardUsd = ParseCsvNullableDouble(GetCsvField(headers, fields, "estimated_reward_usd"));
            row.BalanceDeltaUsd = ParseCsvNullableDouble(GetCsvField(headers, fields, "balance_delta_usd"));
            int.TryParse(GetCsvField(headers, fields, "observation_count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out row.ObservationCount);
            row.LastSeenAtLocal = ParseCsvDateTimeOffset(GetCsvField(headers, fields, "last_seen_at_local"));
            row.SourceLog = GetCsvField(headers, fields, "source_log");
            return row;
        }
    }

    private static string FormatDateTimeOffset(DateTimeOffset? value)
    {
        return value.HasValue ? value.Value.ToString("o", CultureInfo.InvariantCulture) : "";
    }

    private static string GetCsvField(string[] headers, string[] fields, string name)
    {
        var index = Array.IndexOf(headers, name);
        return index >= 0 && fields.Length > index ? fields[index] : "";
    }

    private static DateTimeOffset? ParseCsvDateTimeOffset(string value)
    {
        DateTimeOffset parsed;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed)
            ? (DateTimeOffset?)parsed
            : null;
    }

    private static double? ParseCsvNullableDouble(string value)
    {
        double parsed;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
            ? (double?)parsed
            : null;
    }
}
