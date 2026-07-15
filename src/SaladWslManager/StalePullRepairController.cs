using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

internal static partial class Program
{
    private static readonly TimeSpan StalePullMinimumAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan IdleBowlRepairMinimumAge = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ServiceRepairResultTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StalePullClearTimeout = TimeSpan.FromSeconds(45);

    private static void RequestSaladBowlRepairFromStatusWindow()
    {
        if (Interlocked.CompareExchange(ref saladBowlRepairRunning, 1, 0) != 0)
        {
            MessageBox.Show(
                statusWindow,
                "A SaladBowl repair is already running.",
                "Salad WSL Manager",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // Re-run diagnostics at click time. They remain advisory because a Bowl session
        // can look healthy in logs yet recover only after a user-requested restart.
        var diagnosis = GetSaladBowlRepairDiagnosis(
            GetRecentSaladLogSnapshot(),
            IsSaladTrayProcessRunning(),
            GetServiceState(true),
            DateTimeOffset.Now);
        if (!diagnosis.Eligible)
        {
            diagnosis = SaladBowlRepairDiagnosis.ManualCandidate(diagnosis.Message);
        }

        var confirmation = MessageBox.Show(
            statusWindow,
            diagnosis.ConfirmationText +
            "\r\n\r\nSalad.exe and WSL will not be explicitly stopped.",
            "Repair SaladBowl",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            Interlocked.Exchange(ref saladBowlRepairRunning, 0);
            UpdateSaladBowlRepairButton(diagnosis);
            return;
        }

        SetSaladBowlRepairRunningState(true);
        Log("salad_bowl_repair_requested kind=" + diagnosis.Kind + " detail=" + diagnosis.LogDetail);

        ThreadPool.QueueUserWorkItem(delegate
        {
            var result = RunSaladBowlRepair(diagnosis);
            Interlocked.Exchange(ref saladBowlRepairRunning, 0);
            PostToUi(delegate
            {
                SafeTick();
                MessageBox.Show(
                    statusWindow,
                    result.Message,
                    "Salad WSL Manager",
                    MessageBoxButtons.OK,
                    result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            });
        });
    }

    private static ServiceRepairUiResult RunSaladBowlRepair(SaladBowlRepairDiagnosis diagnosis)
    {
        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            var helperPath = Path.Combine(
                Path.GetDirectoryName(Application.ExecutablePath),
                "SaladWslManager.ServiceRepairHelper.exe");
            if (!File.Exists(helperPath))
            {
                Log("stale_pull_repair_helper_missing path=" + helperPath);
                return ServiceRepairUiResult.Failed("Service repair helper is missing.");
            }

            TryDeleteServiceRepairResult();
            var args = new[]
            {
                "--request-id", requestId,
                "--result", ServiceRepairResultPath,
                "--log", ServiceRepairLogPath
            };
            var startInfo = new ProcessStartInfo();
            startInfo.FileName = helperPath;
            startInfo.Arguments = JoinArguments(args);
            startInfo.UseShellExecute = true;
            startInfo.Verb = "runas";
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            var process = Process.Start(startInfo);
            if (process != null)
            {
                process.Dispose();
            }

            Log("stale_pull_repair_helper_started request_id=" + requestId);
        }
        catch (Exception ex)
        {
            Log("stale_pull_repair_helper_start_error request_id=" + requestId + " error=" + ex.Message);
            return ServiceRepairUiResult.Failed("Service repair was cancelled or could not start.\r\n" + ex.Message);
        }

        ServiceRepairResult helperResult;
        if (!WaitForServiceRepairResult(requestId, ServiceRepairResultTimeout, out helperResult))
        {
            Log("stale_pull_repair_result_timeout request_id=" + requestId);
            return ServiceRepairUiResult.Failed("Timed out waiting for SaladBowl to restart.");
        }

        if (!helperResult.Success)
        {
            Log("stale_pull_repair_failed request_id=" + requestId + " message=" + helperResult.Message);
            return ServiceRepairUiResult.Failed("SaladBowl restart failed.\r\n" + helperResult.Message);
        }

        cachedServiceStateAt = DateTimeOffset.MinValue;
        if (string.Equals(diagnosis.Kind, "stale_pull", StringComparison.OrdinalIgnoreCase))
        {
            var cleared = WaitForStalePullsToClear(StalePullClearTimeout);
            Log("salad_bowl_repair_completed request_id=" + requestId + " kind=" + diagnosis.Kind + " stale_pulls_cleared=" + cleared);
            return cleared
                ? ServiceRepairUiResult.Succeeded("SaladBowl restarted and the stale Pulling state disappeared.")
                : ServiceRepairUiResult.Succeeded("SaladBowl restarted, but the stale Pulling state could not yet be confirmed as cleared.");
        }

        Log("salad_bowl_repair_completed request_id=" + requestId + " kind=" + diagnosis.Kind);
        return ServiceRepairUiResult.Succeeded(
            "SaladBowl restarted. Workload assignment is controlled by Matrix, so continue monitoring the status window.");
    }

    private static bool WaitForStalePullsToClear(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (!GetStaleOrphanedPulls(GetRecentSaladLogSnapshot()).Detected)
            {
                return true;
            }

            Thread.Sleep(TimeSpan.FromSeconds(2));
        }

        return !GetStaleOrphanedPulls(GetRecentSaladLogSnapshot()).Detected;
    }

    private static SaladBowlRepairDiagnosis GetSaladBowlRepairDiagnosis(
        SaladLogSnapshot snapshot,
        bool saladRunning,
        string serviceState,
        DateTimeOffset now)
    {
        if (!saladRunning)
        {
            return SaladBowlRepairDiagnosis.NotEligible("Salad.exe is not running.");
        }

        if (!string.Equals(serviceState, "RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return SaladBowlRepairDiagnosis.NotEligible("SaladBowl is not running.");
        }

        if (!snapshot.Available || snapshot.Lines == null || snapshot.Lines.Length == 0)
        {
            return SaladBowlRepairDiagnosis.NotEligible("The Salad log is unavailable.");
        }

        var stalePulls = GetStaleOrphanedPulls(snapshot);
        if (stalePulls.Detected)
        {
            var ids = string.Join(", ", stalePulls.Ids);
            return SaladBowlRepairDiagnosis.EligibleCandidate(
                "stale_pull",
                "Orphaned Pulling state older than one hour: " + ids,
                "Restart SaladBowl to clear these stale pulls?\r\n\r\n" + ids,
                "ids=" + string.Join(",", stalePulls.Ids));
        }

        return GetInterruptedPullRepairDiagnosis(snapshot.Lines, now);
    }

    private static SaladBowlRepairDiagnosis GetInterruptedPullRepairDiagnosis(string[] lines, DateTimeOffset now)
    {
        var startIndex = FindLatestSuccessfulStartActiveWorkloadsIndex(lines);
        if (startIndex < 0)
        {
            return SaladBowlRepairDiagnosis.NotEligible("No successful Chop-now request was found.");
        }

        var startAt = TryParseSaladLogPrefixLocalTime(lines[startIndex]);
        if (!startAt.HasValue)
        {
            return SaladBowlRepairDiagnosis.NotEligible("The latest Chop-now request time could not be read.");
        }

        var runningIndex = FindLatestLogLineIndex(lines, "Running State: true");
        var desiredIndex = FindLatestLogLineIndex(lines, "Received desired state from matrix");
        var stateHeaderIndex = FindLatestLogLineIndex(lines, "Workload Instance States:");
        if (runningIndex < startIndex || desiredIndex < startIndex ||
            lines[desiredIndex].IndexOf("0 workloads", StringComparison.OrdinalIgnoreCase) < 0 ||
            stateHeaderIndex < startIndex || StateBlockContainsActiveWorkload(lines, stateHeaderIndex))
        {
            return SaladBowlRepairDiagnosis.NotEligible("SaladBowl has active or newly changing workload state.");
        }

        var age = now - startAt.Value;
        if (age < IdleBowlRepairMinimumAge)
        {
            var remaining = IdleBowlRepairMinimumAge - age;
            return SaladBowlRepairDiagnosis.NotEligible(
                "Waiting for Matrix response (about " + Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " seconds). ");
        }

        string interruptedWorkloadId;
        DateTimeOffset interruptedAt;
        if (!TryFindUnresolvedInterruptedPull(lines, startIndex, out interruptedWorkloadId, out interruptedAt))
        {
            return SaladBowlRepairDiagnosis.NotEligible("Matrix reports zero workloads, but no interrupted Pulling residue was found.");
        }

        // This remains manual even with causal evidence: a failed pull can be
        // cleaned up normally, while automatic restart could disrupt a healthy
        // Matrix idle session that happens to follow the same log sequence.
        return SaladBowlRepairDiagnosis.EligibleCandidate(
            "interrupted_pull",
            "Interrupted pull " + interruptedWorkloadId + " may be blocking Bowl after restart.",
            "An earlier pull for " + interruptedWorkloadId + " disappeared after Salad stopped.\r\n" +
            "Chop now has remained at zero workloads for " + FormatRepairAge(age) + ". Restart SaladBowl?",
            "workload=" + interruptedWorkloadId +
            " interrupted_at=" + interruptedAt.ToString("o", CultureInfo.InvariantCulture) +
            " idle_age_seconds=" + Math.Max(0, (int)age.TotalSeconds).ToString(CultureInfo.InvariantCulture));
    }

    private static bool TryFindUnresolvedInterruptedPull(
        string[] lines,
        int currentStartIndex,
        out string workloadId,
        out DateTimeOffset interruptedAt)
    {
        workloadId = "";
        interruptedAt = DateTimeOffset.MinValue;
        var bowlStartIndex = FindLatestLogLineIndexBefore(
            lines,
            "Application started. Press Ctrl+C to shut down.",
            currentStartIndex);
        var lowerBound = Math.Max(0, bowlStartIndex);
        for (var i = currentStartIndex - 1; i > lowerBound; i--)
        {
            var failed = Regex.Match(
                lines[i],
                @"failed-pulling\s+(?<id>[0-9a-fA-F]{8})-",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            var unapproved = Regex.Match(
                lines[i],
                @"unapproved workloads:\s*(?<id>[0-9a-fA-F]{8})-[^\r\n]*-\s*Pulling",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            var match = failed.Success ? failed : unapproved;
            if (!match.Success)
            {
                continue;
            }

            var candidateId = match.Groups["id"].Value;
            if (!HasRunningFalseBefore(lines, lowerBound, i) ||
                !HasPullingEvidenceBefore(lines, lowerBound, i, candidateId) ||
                HasSuccessfulWorkloadRecovery(lines, i + 1, currentStartIndex, candidateId))
            {
                continue;
            }

            var at = TryParseSaladLogPrefixLocalTime(lines[i]);
            if (!at.HasValue)
            {
                continue;
            }

            workloadId = candidateId;
            interruptedAt = at.Value;
            return true;
        }

        return false;
    }

    private static int FindLatestLogLineIndexBefore(string[] lines, string marker, int exclusiveEnd)
    {
        for (var i = Math.Min(lines.Length, exclusiveEnd) - 1; i >= 0; i--)
        {
            if (lines[i].IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool HasRunningFalseBefore(string[] lines, int lowerBound, int incidentIndex)
    {
        for (var i = incidentIndex; i > lowerBound; i--)
        {
            if (lines[i].IndexOf("Running State: false", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPullingEvidenceBefore(string[] lines, int lowerBound, int incidentIndex, string workloadId)
    {
        for (var i = incidentIndex; i > lowerBound; i--)
        {
            var line = lines[i];
            if (line.IndexOf(workloadId, StringComparison.OrdinalIgnoreCase) >= 0 &&
                (line.IndexOf("WLInstanceStatePulling", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("PullingInstallEvent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf(":Pulling(", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSuccessfulWorkloadRecovery(
        string[] lines,
        int startIndex,
        int exclusiveEnd,
        string workloadId)
    {
        for (var i = startIndex; i < exclusiveEnd; i++)
        {
            var line = lines[i];
            if (line.IndexOf(workloadId, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (line.IndexOf("Successfully went through the install pipeline", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("WLStartedStatusEvent", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int FindLatestSuccessfulStartActiveWorkloadsIndex(string[] lines)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.IndexOf("Request finished", StringComparison.OrdinalIgnoreCase) >= 0 &&
                line.IndexOf("StartActiveWorkloads", StringComparison.OrdinalIgnoreCase) >= 0 &&
                Regex.IsMatch(line, @"\s200\s", RegexOptions.CultureInvariant))
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

    private static string FormatRepairAge(TimeSpan age)
    {
        return age.TotalMinutes >= 60
            ? Math.Floor(age.TotalHours).ToString("0", CultureInfo.InvariantCulture) + "h " + age.Minutes.ToString(CultureInfo.InvariantCulture) + "m"
            : Math.Max(1, (int)Math.Floor(age.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + "m";
    }

    private static StalePullSnapshot GetStaleOrphanedPulls(SaladLogSnapshot snapshot)
    {
        if (!snapshot.Available || snapshot.Lines == null)
        {
            return StalePullSnapshot.None;
        }

        var desiredIndex = FindLatestLogLineIndex(snapshot.Lines, "Received desired state from matrix");
        if (desiredIndex < 0 ||
            snapshot.Lines[desiredIndex].IndexOf("0 workloads", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return StalePullSnapshot.None;
        }

        var stateHeaderIndex = FindLatestLogLineIndex(snapshot.Lines, "Workload Instance States:");
        if (stateHeaderIndex < 0)
        {
            return StalePullSnapshot.None;
        }

        var ids = new List<string>();
        for (var i = stateHeaderIndex + 1; i < snapshot.Lines.Length; i++)
        {
            var line = snapshot.Lines[i];
            if (Regex.IsMatch(line, @"^\d{4}-\d{2}-\d{2}\s", RegexOptions.CultureInvariant))
            {
                break;
            }

            var match = Regex.Match(
                line,
                @"^\s*(?<id>[0-9a-fA-F]{8}):\s+\[[^\]]+\]:Pulling\((?<age>[^\)]+)\)",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            TimeSpan age;
            if (TryParsePullAge(match.Groups["age"].Value, out age) && age >= StalePullMinimumAge)
            {
                ids.Add(match.Groups["id"].Value);
            }
        }

        return ids.Count == 0 ? StalePullSnapshot.None : new StalePullSnapshot(ids.ToArray());
    }

    private static bool TryParsePullAge(string text, out TimeSpan age)
    {
        age = TimeSpan.Zero;
        var match = Regex.Match(
            text ?? "",
            @"^(?:(?<days>\d+)d:)?(?<hours>\d+)h(?::(?<minutes>\d+)m)?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        int days;
        int hours;
        int minutes;
        int.TryParse(match.Groups["days"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out days);
        int.TryParse(match.Groups["hours"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out hours);
        int.TryParse(match.Groups["minutes"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes);
        age = TimeSpan.FromDays(days) + TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
        return true;
    }

    private static bool WaitForServiceRepairResult(string requestId, TimeSpan timeout, out ServiceRepairResult result)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (TryReadServiceRepairResult(requestId, out result))
            {
                return true;
            }

            Thread.Sleep(500);
        }

        result = new ServiceRepairResult(false, "No result was returned.");
        return false;
    }

    private static bool TryReadServiceRepairResult(string requestId, out ServiceRepairResult result)
    {
        result = new ServiceRepairResult(false, "");
        try
        {
            if (!File.Exists(ServiceRepairResultPath))
            {
                return false;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(ServiceRepairResultPath, Encoding.UTF8))
            {
                var separator = line.IndexOf('=');
                if (separator > 0)
                {
                    values[line.Substring(0, separator)] = line.Substring(separator + 1);
                }
            }

            string actualRequestId;
            string status;
            if (!values.TryGetValue("request_id", out actualRequestId) ||
                !string.Equals(actualRequestId, requestId, StringComparison.OrdinalIgnoreCase) ||
                !values.TryGetValue("status", out status))
            {
                return false;
            }

            string message;
            values.TryGetValue("message", out message);
            result = new ServiceRepairResult(
                string.Equals(status, "success", StringComparison.OrdinalIgnoreCase),
                string.IsNullOrEmpty(message) ? status : message);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteServiceRepairResult()
    {
        try
        {
            if (File.Exists(ServiceRepairResultPath))
            {
                File.Delete(ServiceRepairResultPath);
            }
        }
        catch (Exception ex)
        {
            Log("stale_pull_repair_result_delete_error " + ex.Message);
        }
    }

    private static void SetSaladBowlRepairRunningState(bool running)
    {
        if (saladBowlRepairButton == null || saladBowlRepairButton.IsDisposed)
        {
            return;
        }

        saladBowlRepairButton.Enabled = !running;
        saladBowlRepairButton.Text = running ? "Working..." : "Repair";
    }

    private static void UpdateSaladBowlRepairButton(SaladBowlRepairDiagnosis diagnosis)
    {
        if (saladBowlRepairButton == null || saladBowlRepairButton.IsDisposed || saladBowlRepairRunning != 0)
        {
            return;
        }

        saladBowlRepairButton.Text = "Repair";
        saladBowlRepairButton.Enabled = true;
        if (saladBowlRepairToolTip != null)
        {
            saladBowlRepairToolTip.SetToolTip(
                saladBowlRepairButton,
                diagnosis.Message + (diagnosis.Eligible ? "" : " Click to restart SaladBowl manually."));
        }
    }

    private struct StalePullSnapshot
    {
        public static readonly StalePullSnapshot None = new StalePullSnapshot(new string[0]);
        public readonly string[] Ids;
        public bool Detected { get { return Ids != null && Ids.Length > 0; } }

        public StalePullSnapshot(string[] ids)
        {
            Ids = ids ?? new string[0];
        }
    }

    private struct ServiceRepairResult
    {
        public readonly bool Success;
        public readonly string Message;

        public ServiceRepairResult(bool success, string message)
        {
            Success = success;
            Message = message ?? "";
        }
    }

    private struct SaladBowlRepairDiagnosis
    {
        public readonly bool Eligible;
        public readonly string Kind;
        public readonly string Message;
        public readonly string ConfirmationText;
        public readonly string LogDetail;

        private SaladBowlRepairDiagnosis(
            bool eligible,
            string kind,
            string message,
            string confirmationText,
            string logDetail)
        {
            Eligible = eligible;
            Kind = kind ?? "none";
            Message = message ?? "No repair condition detected.";
            ConfirmationText = confirmationText ?? "Restart SaladBowl?";
            LogDetail = logDetail ?? "";
        }

        public static SaladBowlRepairDiagnosis NotEligible(string message)
        {
            return new SaladBowlRepairDiagnosis(false, "none", message, "", "");
        }

        public static SaladBowlRepairDiagnosis EligibleCandidate(
            string kind,
            string message,
            string confirmationText,
            string logDetail)
        {
            return new SaladBowlRepairDiagnosis(true, kind, message, confirmationText, logDetail);
        }

        public static SaladBowlRepairDiagnosis ManualCandidate(string diagnosticMessage)
        {
            var message = string.IsNullOrWhiteSpace(diagnosticMessage)
                ? "No automatic repair condition was detected."
                : diagnosticMessage;
            return new SaladBowlRepairDiagnosis(
                true,
                "manual",
                message,
                message + "\r\n\r\nRestarting SaladBowl can interrupt an active workload. Restart manually?",
                "diagnostic=" + OneLine(message));
        }
    }

    private struct ServiceRepairUiResult
    {
        public readonly bool Success;
        public readonly string Message;

        private ServiceRepairUiResult(bool success, string message)
        {
            Success = success;
            Message = message ?? "";
        }

        public static ServiceRepairUiResult Succeeded(string message)
        {
            return new ServiceRepairUiResult(true, message);
        }

        public static ServiceRepairUiResult Failed(string message)
        {
            return new ServiceRepairUiResult(false, message);
        }
    }
}
