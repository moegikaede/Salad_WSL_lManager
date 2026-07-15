using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

internal static partial class Program
{
    // Keep reward ownership and aggregation together so lifecycle persistence remains source-neutral.
    private static bool MergeWorkloadRewards(System.Collections.Generic.Dictionary<string, WorkloadHistoryRow> rows)
    {
        if (!File.Exists(WorkloadObservationsCsvPath))
        {
            return false;
        }

        var rewards = ReadWorkloadRewardAggregates(rows);
        var changed = false;
        foreach (var key in rows.Keys.ToArray())
        {
            var row = rows[key];
            WorkloadRewardAggregate reward;
            if (!rewards.TryGetValue(key, out reward))
            {
                reward = new WorkloadRewardAggregate();
            }

            if (SetNullableIfChanged(ref row.EstimatedRewardUsd, reward.EstimatedRewardUsd) |
                SetNullableIfChanged(ref row.BalanceDeltaUsd, reward.BalanceDeltaUsd) |
                SetIntIfChanged(ref row.ObservationCount, reward.ObservationCount))
            {
                rows[key] = row;
                changed = true;
            }
        }

        return changed;
    }

    private static System.Collections.Generic.Dictionary<string, WorkloadRewardAggregate> ReadWorkloadRewardAggregates(
        System.Collections.Generic.Dictionary<string, WorkloadHistoryRow> rows)
    {
        var result = new System.Collections.Generic.Dictionary<string, WorkloadRewardAggregate>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in ReadAttributedWorkloadRewardSamples(rows))
        {
            var touchedOwners = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sample.EstimatedRewardUsd.HasValue)
            {
                AddEstimatedWorkloadReward(result, sample.EstimatedOwner, sample.EstimatedRewardUsd.Value);
                if (!string.IsNullOrEmpty(sample.EstimatedOwner))
                {
                    touchedOwners.Add(sample.EstimatedOwner);
                }
            }

            if (sample.BalanceRewardUsd.HasValue)
            {
                AddBalanceWorkloadReward(result, sample.BalanceOwner, sample.BalanceRewardUsd.Value);
                if (!string.IsNullOrEmpty(sample.BalanceOwner))
                {
                    touchedOwners.Add(sample.BalanceOwner);
                }
            }

            foreach (var owner in touchedOwners)
            {
                WorkloadRewardAggregate aggregate;
                if (!result.TryGetValue(owner, out aggregate))
                {
                    aggregate = new WorkloadRewardAggregate();
                }

                aggregate.ObservationCount++;
                result[owner] = aggregate;
            }
        }

        return result;
    }

    private static System.Collections.Generic.List<AttributedWorkloadRewardSample> ReadAttributedWorkloadRewardSamples(
        System.Collections.Generic.Dictionary<string, WorkloadHistoryRow> rows)
    {
        var result = new System.Collections.Generic.List<AttributedWorkloadRewardSample>();
        try
        {
            if (!File.Exists(WorkloadObservationsCsvPath))
            {
                return result;
            }

            using (var stream = new FileStream(WorkloadObservationsCsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                var headers = ParseCsvLine(reader.ReadLine() ?? "");
                var workloadIndex = Array.IndexOf(headers, "workload_id");
                var capturedAtIndex = Array.IndexOf(headers, "captured_at_local");
                var earningIndex = Array.IndexOf(headers, "earning_usd_per_5min");
                var balanceDeltaIndex = Array.IndexOf(headers, "balance_delta_usd");
                if (workloadIndex < 0 || capturedAtIndex < 0)
                {
                    return result;
                }

                string previousEstimatedOwner = "";
                double? previousEstimatedReward = null;
                var previousEstimatedAt = DateTimeOffset.MinValue;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var fields = ParseCsvLine(line);
                    if (fields.Length <= workloadIndex || string.IsNullOrWhiteSpace(fields[workloadIndex]))
                    {
                        continue;
                    }

                    DateTimeOffset capturedAt;
                    if (fields.Length <= capturedAtIndex ||
                        !DateTimeOffset.TryParse(fields[capturedAtIndex], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out capturedAt))
                    {
                        continue;
                    }

                    capturedAt = capturedAt.ToLocalTime();
                    var estimatedOwner = FindObservationWorkloadOwner(rows, fields[workloadIndex], capturedAt);
                    double value;
                    double? estimatedReward = null;
                    if (earningIndex >= 0 && fields.Length > earningIndex &&
                        double.TryParse(fields[earningIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    {
                        estimatedReward = value;
                    }

                    double? balanceReward = null;
                    var balanceOwner = estimatedOwner;
                    var balanceAttributedAt = capturedAt;
                    if (balanceDeltaIndex >= 0 && fields.Length > balanceDeltaIndex &&
                        double.TryParse(fields[balanceDeltaIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    {
                        balanceReward = value;
                        // Wallet refresh trails the estimate by one observation.
                        // Preserve both its owner and original time so history
                        // totals and the blue chart cannot disagree at a boundary.
                        if (previousEstimatedReward.HasValue &&
                            Math.Abs(previousEstimatedReward.Value - value) <= 0.000001 &&
                            !string.IsNullOrEmpty(previousEstimatedOwner))
                        {
                            balanceOwner = previousEstimatedOwner;
                            balanceAttributedAt = previousEstimatedAt;
                        }
                    }

                    result.Add(new AttributedWorkloadRewardSample(
                        capturedAt,
                        estimatedOwner,
                        estimatedReward,
                        balanceAttributedAt,
                        balanceOwner,
                        balanceReward));

                    if (estimatedReward.HasValue)
                    {
                        previousEstimatedOwner = estimatedOwner;
                        previousEstimatedReward = estimatedReward.Value;
                        previousEstimatedAt = capturedAt;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("workload_reward_attribution_error " + ex.Message);
        }

        return result;
    }

    private static void AddEstimatedWorkloadReward(
        System.Collections.Generic.Dictionary<string, WorkloadRewardAggregate> result,
        string owner,
        double value)
    {
        if (string.IsNullOrEmpty(owner))
        {
            return;
        }

        WorkloadRewardAggregate aggregate;
        result.TryGetValue(owner, out aggregate);
        aggregate.EstimatedRewardUsd += value;
        result[owner] = aggregate;
    }

    private static void AddBalanceWorkloadReward(
        System.Collections.Generic.Dictionary<string, WorkloadRewardAggregate> result,
        string owner,
        double value)
    {
        if (string.IsNullOrEmpty(owner))
        {
            return;
        }

        WorkloadRewardAggregate aggregate;
        result.TryGetValue(owner, out aggregate);
        aggregate.BalanceDeltaUsd += value;
        result[owner] = aggregate;
    }

    private static string FindObservationWorkloadOwner(
        System.Collections.Generic.Dictionary<string, WorkloadHistoryRow> rows,
        string workloadId,
        DateTimeOffset capturedAt)
    {
        var direct = FindWorkloadInstanceAt(rows, workloadId, capturedAt);
        if (!string.IsNullOrEmpty(direct))
        {
            return direct;
        }

        var active = FindActiveWorkloadInstanceAt(rows, capturedAt);
        var stopped = FindRecentlyStoppedWorkloadInstanceAt(rows, capturedAt);
        if (!string.IsNullOrEmpty(active) && !string.IsNullOrEmpty(stopped))
        {
            // Old history can contain a malformed long-running interval that
            // overlaps newer instances. Compare transition boundaries instead
            // of blindly trusting either "active" or "stopped": a newer start
            // means the next workload owns the sample, otherwise the latest
            // stop owns the trailing reward.
            return rows[active].StartedAtLocal.Value > rows[stopped].StoppedAtLocal.Value
                ? active
                : stopped;
        }

        if (!string.IsNullOrEmpty(active))
        {
            return active;
        }

        // During the gap between workloads, the API can still report the last
        // completed five-minute earning under the newly assigned base id.
        return stopped;
    }

    private static string FindRecentlyStoppedWorkloadInstanceAt(
        System.Collections.Generic.Dictionary<string, WorkloadHistoryRow> rows,
        DateTimeOffset capturedAt)
    {
        var selectedKey = "";
        var selectedStop = DateTimeOffset.MinValue;
        foreach (var pair in rows)
        {
            var stoppedAt = pair.Value.StoppedAtLocal;
            if (!stoppedAt.HasValue || stoppedAt.Value > capturedAt ||
                capturedAt - stoppedAt.Value > WorkloadRewardAttributionGrace ||
                stoppedAt.Value < selectedStop)
            {
                continue;
            }

            selectedStop = stoppedAt.Value;
            selectedKey = pair.Key;
        }

        return selectedKey;
    }

    private static string FindActiveWorkloadInstanceAt(
        System.Collections.Generic.Dictionary<string, WorkloadHistoryRow> rows,
        DateTimeOffset capturedAt)
    {
        var selectedKey = "";
        var selectedStart = DateTimeOffset.MinValue;
        foreach (var pair in rows)
        {
            var row = pair.Value;
            if (!row.StartedAtLocal.HasValue || row.StartedAtLocal.Value > capturedAt ||
                row.StoppedAtLocal.HasValue && capturedAt >= row.StoppedAtLocal.Value)
            {
                continue;
            }

            // A later start wins only for malformed overlapping history. This
            // mirrors direct-id resolution and avoids nondeterministic owners.
            if (row.StartedAtLocal.Value >= selectedStart)
            {
                selectedStart = row.StartedAtLocal.Value;
                selectedKey = pair.Key;
            }
        }

        return selectedKey;
    }

    private static string FindWorkloadInstanceAt(
        System.Collections.Generic.Dictionary<string, WorkloadHistoryRow> rows,
        string workloadId,
        DateTimeOffset capturedAt)
    {
        var selectedKey = "";
        var selectedStart = DateTimeOffset.MinValue;
        foreach (var pair in rows)
        {
            var row = pair.Value;
            if (!string.Equals(row.WorkloadId, workloadId, StringComparison.OrdinalIgnoreCase) ||
                !row.StartedAtLocal.HasValue || row.StartedAtLocal.Value > capturedAt ||
                row.StoppedAtLocal.HasValue && capturedAt >= row.StoppedAtLocal.Value)
            {
                continue;
            }

            // If malformed historic rows overlap, the latest started instance is the least ambiguous owner.
            if (row.StartedAtLocal.Value >= selectedStart)
            {
                selectedStart = row.StartedAtLocal.Value;
                selectedKey = pair.Key;
            }
        }

        return selectedKey;
    }

    private static bool SetNullableIfChanged(ref double? target, double value)
    {
        if (target.HasValue && Math.Abs(target.Value - value) < 0.00000001)
        {
            return false;
        }

        target = value;
        return true;
    }

    private static bool SetIntIfChanged(ref int target, int value)
    {
        if (target == value)
        {
            return false;
        }

        target = value;
        return true;
    }

    private struct WorkloadRewardAggregate
    {
        public double EstimatedRewardUsd;
        public double BalanceDeltaUsd;
        public int ObservationCount;
    }

    private struct AttributedWorkloadRewardSample
    {
        public readonly DateTimeOffset CapturedAt;
        public readonly string EstimatedOwner;
        public readonly double? EstimatedRewardUsd;
        public readonly DateTimeOffset BalanceAttributedAt;
        public readonly string BalanceOwner;
        public readonly double? BalanceRewardUsd;

        public AttributedWorkloadRewardSample(
            DateTimeOffset capturedAt,
            string estimatedOwner,
            double? estimatedRewardUsd,
            DateTimeOffset balanceAttributedAt,
            string balanceOwner,
            double? balanceRewardUsd)
        {
            CapturedAt = capturedAt;
            EstimatedOwner = estimatedOwner ?? "";
            EstimatedRewardUsd = estimatedRewardUsd;
            BalanceAttributedAt = balanceAttributedAt;
            BalanceOwner = balanceOwner ?? "";
            BalanceRewardUsd = balanceRewardUsd;
        }
    }
}
