using System;
using System.Text.RegularExpressions;

internal enum PendingQuitLogDecision
{
    KeepWaiting,
    SafeToQuit
}

internal static class PendingQuitLogAnalyzer
{
    public static PendingQuitLogDecision Evaluate(string[] lines, string workloadId, string instanceId)
    {
        if (lines == null || string.IsNullOrEmpty(workloadId))
        {
            return PendingQuitLogDecision.KeepWaiting;
        }

        var pauseRequestIndex = FindLatestSuccessfulGrpcRequestIndex(lines, "StopActiveWorkloads");
        if (pauseRequestIndex < 0)
        {
            return PendingQuitLogDecision.KeepWaiting;
        }

        var stoppedIndex = FindStoppedIndex(lines, workloadId, instanceId, pauseRequestIndex);
        if (stoppedIndex < 0)
        {
            return PendingQuitLogDecision.KeepWaiting;
        }

        var latestDesiredIndex = FindLatestLineIndex(lines, "Received desired state from matrix");
        var latestStateHeaderIndex = FindLatestLineIndex(lines, "Workload Instance States:");
        var latestRunningStateIndex = FindLatestRunningStateIndex(lines);
        if (latestDesiredIndex <= pauseRequestIndex ||
            latestRunningStateIndex <= pauseRequestIndex ||
            lines[latestDesiredIndex].IndexOf("0 workloads", StringComparison.OrdinalIgnoreCase) < 0 ||
            lines[latestRunningStateIndex].IndexOf("Running State: false", StringComparison.OrdinalIgnoreCase) < 0 ||
            latestStateHeaderIndex > stoppedIndex && StateBlockContainsActiveWorkload(lines, latestStateHeaderIndex))
        {
            return PendingQuitLogDecision.KeepWaiting;
        }

        return PendingQuitLogDecision.SafeToQuit;
    }

    private static int FindStoppedIndex(string[] lines, string workloadId, string instanceId, int requestIndex)
    {
        var instancePattern = string.IsNullOrEmpty(instanceId) ? @"[^\]]+" : Regex.Escape(instanceId);
        var stoppedPattern = @"^\s*" + Regex.Escape(workloadId) + @":\s+\[" + instancePattern + @"\]:Stopped\(";
        var stoppedEventPattern = @"WLStoppedStatusEvent.*\b" + Regex.Escape(workloadId) + @"\b";
        var normalizedStoppedPattern = @"WLInstanceStateStopped\(\s*" + Regex.Escape(workloadId) + @"\b";
        for (var i = lines.Length - 1; i > requestIndex; i--)
        {
            if (Regex.IsMatch(lines[i], stoppedPattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) ||
                Regex.IsMatch(lines[i], stoppedEventPattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) ||
                Regex.IsMatch(lines[i], normalizedStoppedPattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindLatestSuccessfulGrpcRequestIndex(string[] lines, string method)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.IndexOf("Request finished", StringComparison.OrdinalIgnoreCase) >= 0 &&
                line.IndexOf(method, StringComparison.OrdinalIgnoreCase) >= 0 &&
                Regex.IsMatch(line, @"\s200\s", RegexOptions.CultureInvariant))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindLatestLineIndex(string[] lines, string marker)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindLatestRunningStateIndex(string[] lines)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].IndexOf("Running State: true", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lines[i].IndexOf("Running State: false", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool StateBlockContainsActiveWorkload(string[] lines, int stateHeaderIndex)
    {
        for (var i = stateHeaderIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Regex.IsMatch(line, @"^\d{4}-\d{2}-\d{2}\s", RegexOptions.CultureInvariant))
            {
                break;
            }

            if (Regex.IsMatch(
                line,
                @"^\s*[0-9a-fA-F]{8}:\s+\[[^\]]+\]:(?:Running|Pulling|Starting)\(",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
