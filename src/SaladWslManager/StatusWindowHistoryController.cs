using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

internal static partial class Program
{
    // Keep chart projection and history navigation together so UI lifecycle code stays focused on the window.
    private static void UpdateEarningsHistoryChart()
    {
        if (earningsHistoryChart == null || earningsHistoryChart.IsDisposed)
        {
            return;
        }

        var end = EnsureHistoryEnd();
        var start = end.AddHours(-24);
        if (earningsHistoryRangeLabel != null)
        {
            earningsHistoryRangeLabel.Text = start.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
        }

        var hourly = LoadHourlyEarnings(start, end);
        var selectedWorkload = GetSelectedOrCurrentWorkloadHistoryRow();
        var selectedHourly = selectedWorkload.HasValue
            ? LoadHourlyEarningsForWorkload(start, end, selectedWorkload.Value)
            : new System.Collections.Generic.Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var series = earningsHistoryChart.Series["Earnings"];
        var selectedSeries = earningsHistoryChart.Series["Selected workload"];
        series.Points.Clear();
        selectedSeries.Points.Clear();
        var maximumHourlyValue = hourly.Count > 0 ? hourly.Values.Max() : 0;
        var estimatedPlotHeight = Math.Max(80, earningsHistoryChart.ClientSize.Height - 55);
        // Auto-scaling leaves headroom above the largest column. Convert the
        // requested twelve pixels into Y units without changing column totals.
        var minimumVisibleSelectedValue = maximumHourlyValue * 1.15 * 12.0 / estimatedPlotHeight;
        for (var i = 0; i < 24; i++)
        {
            var hour = start.AddHours(i);
            double value;
            if (!hourly.TryGetValue(HourKey(hour), out value))
            {
                value = 0;
            }

            double selectedValue;
            selectedHourly.TryGetValue(HourKey(hour), out selectedValue);
            selectedValue = Math.Max(0, Math.Min(value, selectedValue));
            var displayedSelectedValue = selectedValue > 0
                ? Math.Min(value, Math.Max(selectedValue, minimumVisibleSelectedValue))
                : 0;
            var pointIndex = series.Points.AddY(value - displayedSelectedValue);
            var selectedPointIndex = selectedSeries.Points.AddY(displayedSelectedValue);
            series.Points[pointIndex].AxisLabel = hour.ToString("HH", CultureInfo.CurrentCulture);
            series.Points[pointIndex].ToolTip =
                hour.ToString("yyyy-MM-dd HH:00", CultureInfo.CurrentCulture) +
                " $" +
                value.ToString("0.0000", CultureInfo.InvariantCulture);
            selectedSeries.Points[selectedPointIndex].AxisLabel = hour.ToString("HH", CultureInfo.CurrentCulture);
            selectedSeries.Points[selectedPointIndex].ToolTip = selectedWorkload.HasValue
                ? FormatWorkloadHistoryDisplayId(selectedWorkload.Value) + " $" + selectedValue.ToString("0.0000", CultureInfo.InvariantCulture)
                : "";
        }

        earningsHistoryChart.Invalidate();
        lastStatusWindowChartUpdate = DateTimeOffset.Now;
    }

    private static void NavigateSelectedWorkload(int direction)
    {
        var rows = GetNavigableWorkloadHistoryRows();
        if (rows.Count == 0)
        {
            return;
        }

        var index = FindSelectedWorkloadIndex(rows);
        var currentIndex = FindCurrentWorkloadHistoryIndex(rows);
        var decision = WorkloadNavigationPolicy.Resolve(rows.Count, index, currentIndex, direction);
        var target = decision.TargetIndex;
        if (decision.UseLiveMode && decision.AlignTargetRow)
        {
            // Live mode uses an empty key, but its graph date must be aligned
            // before clearing the explicit selection or the status and chart
            // can point at different calendar days.
            AlignEarningsHistoryToWorkload(rows[target]);
            selectedWorkloadHistoryKey = "";
        }
        else if (decision.UseLiveMode)
        {
            // An unrewarded live workload is a virtual position after the last
            // navigable row. It has no reward-bearing history row to align.
            selectedWorkloadHistoryKey = "";
        }
        else
        {
            selectedWorkloadHistoryKey = rows[target].Key;
            AlignEarningsHistoryToWorkload(rows[target]);
        }

        UpdateStatusWindowCells(lastAppStateSnapshotAvailable ? lastAppStateSnapshot.ToStatusString() : "");
        UpdateEarningsHistoryChart();
    }

    private static void AlignEarningsHistoryToWorkload(WorkloadHistoryRow workload)
    {
        var workloadAt = workload.StartedAtLocal ?? workload.LastSeenAtLocal;
        if (!workloadAt.HasValue)
        {
            return;
        }

        var currentEnd = EnsureHistoryEnd();
        var currentStart = currentEnd.AddHours(-24);
        var workloadStart = workloadAt.Value.ToLocalTime();
        var workloadEnd = (workload.StoppedAtLocal ?? DateTimeOffset.Now).ToLocalTime();
        // A workload can span several calendar days. Keep the current day when
        // its execution interval overlaps the visible graph; jumping to its
        // first day would hide later-day earnings from WL navigation.
        if (workloadStart < currentEnd && workloadEnd > currentStart)
        {
            return;
        }

        var localAt = workloadAt.Value.ToLocalTime();
        // History pages are midnight-based; selecting an off-page workload opens its local calendar day.
        var dayStart = new DateTimeOffset(
            localAt.Year,
            localAt.Month,
            localAt.Day,
            0,
            0,
            0,
            localAt.Offset);
        earningsHistoryEnd = dayStart.AddDays(1);
    }

    private static void UpdateWorkloadNavigationButtons()
    {
        if (previousWorkloadButton == null || nextWorkloadButton == null)
        {
            return;
        }

        var rows = GetNavigableWorkloadHistoryRows();
        var index = FindSelectedWorkloadIndex(rows);
        var currentIndex = FindCurrentWorkloadHistoryIndex(rows);
        var maximumIndex = currentIndex < 0 ? rows.Count : rows.Count - 1;
        previousWorkloadButton.Enabled = rows.Count > 0 && index > 0;
        nextWorkloadButton.Enabled = rows.Count > 0 && index < maximumIndex;
    }

    private static System.Collections.Generic.List<WorkloadHistoryRow> GetNavigableWorkloadHistoryRows()
    {
        var rows = new System.Collections.Generic.List<WorkloadHistoryRow>(ReadWorkloadHistoryRows().Values);
        rows.RemoveAll(delegate(WorkloadHistoryRow row)
        {
            if (string.IsNullOrEmpty(row.WorkloadId) || string.IsNullOrEmpty(row.InstanceId))
            {
                return true;
            }

            // Navigation is for earnings inspection, so workloads with no attributable reward add no useful stop.
            var estimatedReward = row.EstimatedRewardUsd ?? 0;
            var balanceReward = row.BalanceDeltaUsd ?? 0;
            return estimatedReward <= 0 && balanceReward <= 0;
        });
        rows.Sort(delegate(WorkloadHistoryRow left, WorkloadHistoryRow right)
        {
            return GetWorkloadHistorySortTime(left).CompareTo(GetWorkloadHistorySortTime(right));
        });
        return rows;
    }

    private static DateTimeOffset GetWorkloadHistorySortTime(WorkloadHistoryRow row)
    {
        return row.StartedAtLocal ?? row.LastSeenAtLocal ?? DateTimeOffset.MinValue;
    }

    private static int FindSelectedWorkloadIndex(System.Collections.Generic.List<WorkloadHistoryRow> rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return -1;
        }

        if (!string.IsNullOrEmpty(selectedWorkloadHistoryKey))
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (string.Equals(rows[i].Key, selectedWorkloadHistoryKey, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        var currentIndex = FindCurrentWorkloadHistoryIndex(rows);
        // If the live workload has no attributable reward, preserve it as a
        // virtual position after all navigable rows. This makes the first
        // backward action select the most recent rewarded workload.
        return currentIndex >= 0 ? currentIndex : rows.Count;
    }

    private static int FindCurrentWorkloadHistoryIndex(System.Collections.Generic.List<WorkloadHistoryRow> rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return -1;
        }

        var current = GetRecentSaladWorkloadSnapshot();
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (string.Equals(rows[i].WorkloadId, current.Id, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(current.InstanceId) ||
                    string.Equals(rows[i].InstanceId, current.InstanceId, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return -1;
    }

    private static WorkloadHistoryRow? GetSelectedWorkloadHistoryRow()
    {
        if (string.IsNullOrEmpty(selectedWorkloadHistoryKey))
        {
            return null;
        }

        WorkloadHistoryRow row;
        return ReadWorkloadHistoryRows().TryGetValue(selectedWorkloadHistoryKey, out row)
            ? (WorkloadHistoryRow?)row
            : null;
    }

    private static WorkloadHistoryRow? GetSelectedOrCurrentWorkloadHistoryRow()
    {
        var selected = GetSelectedWorkloadHistoryRow();
        if (selected.HasValue)
        {
            return selected;
        }

        var current = GetRecentSaladWorkloadSnapshot();
        var rows = GetNavigableWorkloadHistoryRows();
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (string.Equals(rows[i].WorkloadId, current.Id, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(current.InstanceId) || string.Equals(rows[i].InstanceId, current.InstanceId, StringComparison.OrdinalIgnoreCase)))
            {
                return rows[i];
            }
        }

        return null;
    }

    private static string FormatWorkloadHistoryDisplayId(WorkloadHistoryRow row)
    {
        return row.WorkloadId.Substring(0, Math.Min(4, row.WorkloadId.Length)) + "-" +
            row.InstanceId.Substring(0, Math.Min(4, row.InstanceId.Length));
    }

    private static System.Collections.Generic.Dictionary<string, double> LoadHourlyEarningsForWorkload(
        DateTimeOffset start,
        DateTimeOffset end,
        WorkloadHistoryRow workload)
    {
        var hourly = new System.Collections.Generic.Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var workloadStart = workload.StartedAtLocal ?? workload.LastSeenAtLocal ?? start;
        var workloadEnd = workload.StoppedAtLocal.HasValue
            ? workload.StoppedAtLocal.Value + WorkloadRewardAttributionGrace
            : DateTimeOffset.Now;
        var rangeStart = workloadStart > start ? workloadStart : start;
        var rangeEnd = workloadEnd < end ? workloadEnd : end;
        if (rangeStart >= rangeEnd)
        {
            return hourly;
        }

        AddHourlyWorkloadEarningsFromCsv(hourly, workload, rangeStart, rangeEnd);
        return hourly;
    }

    private static void AddHourlyWorkloadEarningsFromCsv(
        System.Collections.Generic.Dictionary<string, double> hourly,
        WorkloadHistoryRow workload,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        try
        {
            var historyRows = ReadWorkloadHistoryRows();
            var samples = ReadAttributedWorkloadRewardSamples(historyRows);
            var useEstimated = samples.Any(sample =>
                string.Equals(sample.EstimatedOwner, workload.Key, StringComparison.OrdinalIgnoreCase) &&
                sample.EstimatedRewardUsd.HasValue && sample.EstimatedRewardUsd.Value > 0);
            var lastIncludedAt = DateTimeOffset.MinValue;
            foreach (var sample in samples)
            {
                // Prefer estimates because the green total uses that source.
                // Balance is a fallback only for workloads whose estimates are
                // entirely absent, preventing mixed-source double counting.
                var owner = useEstimated ? sample.EstimatedOwner : sample.BalanceOwner;
                var capturedAt = useEstimated ? sample.CapturedAt : sample.BalanceAttributedAt;
                var reward = useEstimated ? sample.EstimatedRewardUsd : sample.BalanceRewardUsd;
                if (!reward.HasValue || reward.Value <= 0 ||
                    !string.Equals(owner, workload.Key, StringComparison.OrdinalIgnoreCase) ||
                    capturedAt < start || capturedAt >= end ||
                    lastIncludedAt != DateTimeOffset.MinValue && capturedAt - lastIncludedAt < EstimateRefreshInterval)
                {
                    continue;
                }

                var key = HourKey(capturedAt);
                double value;
                hourly.TryGetValue(key, out value);
                hourly[key] = value + reward.Value;
                lastIncludedAt = capturedAt;
            }
        }
        catch (Exception ex)
        {
            Log("workload_earnings_chart_load_error " + ex.Message);
        }
    }

    private static void EnableDoubleBuffering(Control control)
    {
        if (control == null)
        {
            return;
        }

        try
        {
            var property = typeof(Control).GetProperty(
                "DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (property != null)
            {
                property.SetValue(control, true, null);
            }
        }
        catch
        {
        }
    }

    private static DateTimeOffset EnsureHistoryEnd()
    {
        if (earningsHistoryEnd == DateTimeOffset.MinValue)
        {
            earningsHistoryEnd = RoundUpToDay(DateTimeOffset.Now);
        }

        return earningsHistoryEnd;
    }

    private static DateTimeOffset RoundUpToDay(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var rounded = new DateTimeOffset(
            local.Year,
            local.Month,
            local.Day,
            0,
            0,
            0,
            local.Offset);
        if (local.Hour != 0 || local.Minute != 0 || local.Second != 0 || local.Millisecond != 0)
        {
            rounded = rounded.AddDays(1);
        }

        return rounded;
    }

    private static System.Collections.Generic.Dictionary<string, double> LoadHourlyEarnings(DateTimeOffset start, DateTimeOffset end)
    {
        var hourly = new System.Collections.Generic.Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        AddHourlyEarningsFromCsv(hourly, WorkloadObservationsCsvPath, "captured_at_local", "earning_usd_per_5min", start, end);
        var estimated = new System.Collections.Generic.Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        AddHourlyEarningsFromCsv(estimated, EstimatedEarningsCsvPath, "captured_at_local", "earning_usd_per_5min", start, end);
        AddHourlyEarningsFromCsv(estimated, UserEstimatedEarningsCsvPath, "captured_at_local", "earning_usd_per_5min", start, end);
        foreach (var pair in estimated)
        {
            if (!hourly.ContainsKey(pair.Key))
            {
                hourly[pair.Key] = pair.Value;
            }
        }

        return hourly;
    }

    private static void AddHourlyEarningsFromCsv(
        System.Collections.Generic.Dictionary<string, double> hourly,
        string path,
        string timeColumn,
        string valueColumn,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                var headerLine = reader.ReadLine();
                if (headerLine == null)
                {
                    return;
                }

                var headers = ParseCsvLine(headerLine);
                var timeIndex = Array.IndexOf(headers, timeColumn);
                var valueIndex = Array.IndexOf(headers, valueColumn);
                if (timeIndex < 0 || valueIndex < 0)
                {
                    return;
                }

                var lastIncludedAt = DateTimeOffset.MinValue;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var fields = ParseCsvLine(line);
                    if (fields.Length <= Math.Max(timeIndex, valueIndex))
                    {
                        continue;
                    }

                    DateTimeOffset capturedAt;
                    double earning;
                    if (!TryParseDateTimeOffset(fields[timeIndex], out capturedAt) ||
                        !double.TryParse(fields[valueIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out earning))
                    {
                        continue;
                    }

                    capturedAt = capturedAt.ToLocalTime();
                    if (capturedAt < start || capturedAt >= end)
                    {
                        continue;
                    }

                    if (lastIncludedAt != DateTimeOffset.MinValue &&
                        capturedAt - lastIncludedAt < EstimateRefreshInterval)
                    {
                        continue;
                    }

                    var key = HourKey(capturedAt);
                    double current;
                    hourly.TryGetValue(key, out current);
                    hourly[key] = current + earning;
                    lastIncludedAt = capturedAt;
                }
            }
        }
        catch
        {
        }
    }

    private static string HourKey(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        return local.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
    }

    private static bool TryParseDateTimeOffset(string value, out DateTimeOffset result)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result))
        {
            return true;
        }

        var match = Regex.Match(value ?? "", @"(.+\.)(\d{6})\d+(.+)");
        if (match.Success)
        {
            var normalized = match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value;
            return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
        }

        result = DateTimeOffset.MinValue;
        return false;
    }

    private static string[] ParseCsvLine(string line)
    {
        var values = new System.Collections.Generic.List<string>();
        if (line == null)
        {
            return values.ToArray();
        }

        var builder = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        builder.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    builder.Append(ch);
                }

                continue;
            }

            if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == ',')
            {
                values.Add(builder.ToString());
                builder.Length = 0;
            }
            else
            {
                builder.Append(ch);
            }
        }

        values.Add(builder.ToString());
        return values.ToArray();
    }
}
