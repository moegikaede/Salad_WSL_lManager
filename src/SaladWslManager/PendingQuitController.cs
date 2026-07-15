using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal static partial class Program
{
    private static readonly TimeSpan PauseUntilIdleConfirmationTimeout = TimeSpan.FromSeconds(10);

    private static void RequestQuitSaladApp()
    {
        Log("tray_quit_salad_app_clicked");
        var pending = pendingQuitSession.Snapshot();
        if (pending.IsActive)
        {
            if (!pendingQuitSession.TryBeginCancel())
            {
                SetTrayStatus("Salad stop is already closing", System.Drawing.SystemIcons.Warning);
                return;
            }

            if (pending.PauseUntilIdleRequested)
            {
                var resumeResult = CallSaladBowlGrpcHttp2Empty("StartActiveWorkloads");
                Log("salad_app_quit_cancel_resume_result " + resumeResult);
                if (!IsGrpcSuccess(resumeResult))
                {
                    pendingQuitSession.Restore(pending);
                    SetTrayStatus("Stop cancellation failed | " + resumeResult, System.Drawing.SystemIcons.Error);
                    return;
                }
            }

            pendingQuitSession.Clear();
            StopPendingQuitLogWatcher();
            DeletePendingQuitState();
            Log("salad_app_quit_deferred_cancelled");
            UpdateTrayActionChecks(IsSaladTrayProcessRunning(), GetRecentSaladWorkloadState());
            SetTrayStatus("Stop Salad app cancelled", System.Drawing.SystemIcons.Information);
            return;
        }

        var workload = GetRecentSaladWorkloadSnapshot();
        var workloadState = workload.State;
        var distroState = GetDistroState(true);
        pendingQuitSession.BeginPauseRequest(DateTimeOffset.Now);
        if (!RequestPauseUntilIdleForSaladExit("stop_salad_app_clicked"))
        {
            pendingQuitSession.Fail("Pause until idle request failed");
            return;
        }

        if (IsSafeToQuitSaladApp(workloadState, distroState))
        {
            pendingQuitSession.TryEnterClosing();
            if (!StopSaladAppProcesses("salad_app_quit_requested", true))
            {
                pendingQuitSession.Fail("Salad close was aborted");
            }
            return;
        }

        pendingQuitSession.Reserve(workload.Id, workload.InstanceId, "salad_app_quit_when_idle");
        SavePendingQuitState();
        StartPendingQuitLogWatcher(workload.Id, workload.InstanceId);
        Log("salad_app_quit_deferred workload=" + workloadState + " distro=" + distroState);
        UpdateTrayActionChecks(IsSaladTrayProcessRunning(), workloadState);
        UpdateTrayAnimationTimer();
    }

    private static void RestorePendingQuitIfSameWorkload(WorkloadSnapshot currentWorkload)
    {
        if (pendingQuitRestoreChecked || pendingQuitSession.Snapshot().IsActive)
        {
            return;
        }

        PendingQuitState state;
        if (!TryReadPendingQuitState(out state))
        {
            pendingQuitRestoreChecked = true;
            return;
        }

        var sameWorkload = string.Equals(
            state.WorkloadId,
            currentWorkload.Id,
            StringComparison.OrdinalIgnoreCase);
        var completedWhileWorkloadEmpty = string.IsNullOrEmpty(currentWorkload.Id) &&
            state.PauseUntilIdleRequested &&
            IsLatestSaladRunningStatePaused(GetRecentSaladLogSnapshot()) &&
            GetPendingQuitLogDecision(state.WorkloadId, state.InstanceId) == PendingQuitLogDecision.SafeToQuit;
        if (!sameWorkload && !completedWhileWorkloadEmpty)
        {
            if (!string.IsNullOrEmpty(currentWorkload.Id))
            {
                // Never attach a persisted stop request to a successor workload.
                pendingQuitRestoreChecked = true;
                DeletePendingQuitState();
                Log("salad_app_quit_deferred_discarded workload_mismatch saved=" +
                    state.WorkloadId + " current=" + currentWorkload.Id);
            }

            return;
        }

        pendingQuitRestoreChecked = true;
        var restoredReason = string.IsNullOrEmpty(state.Reason) ? "salad_app_quit_when_idle" : state.Reason;
        var restoredInstanceId = state.InstanceId;
        if (string.IsNullOrEmpty(restoredInstanceId) &&
            string.Equals(state.WorkloadId, currentWorkload.Id, StringComparison.OrdinalIgnoreCase))
        {
            // Migrate a legacy reservation once, but never attach an old reservation to a new instance.
            restoredInstanceId = currentWorkload.InstanceId;
        }
        pendingQuitSession.Restore(new PendingQuitSnapshot(
            state.Phase,
            state.WorkloadId,
            restoredInstanceId,
            restoredReason,
            state.PauseUntilIdleRequested,
            state.RequestedAt,
            ""));
        SavePendingQuitState();
        StartPendingQuitLogWatcher(state.WorkloadId, restoredInstanceId);
        Log("salad_app_quit_deferred_restored workload=" + state.WorkloadId + " instance=" + restoredInstanceId);
    }

    private static void SavePendingQuitState()
    {
        try
        {
            var state = pendingQuitSession.Snapshot();
            if (string.IsNullOrEmpty(state.WorkloadId))
            {
                DeletePendingQuitState();
                return;
            }

            Directory.CreateDirectory(AppDir);
            File.WriteAllLines(
                PendingQuitStatePath,
                new[]
                {
                    "phase=" + state.Phase,
                    "requested_at=" + state.RequestedAt.ToString("O"),
                    "workload_id=" + state.WorkloadId,
                    "instance_id=" + state.InstanceId,
                    "reason=" + state.Reason,
                    "pause_until_idle_requested=" + (state.PauseUntilIdleRequested ? "true" : "false")
                },
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log("salad_app_quit_deferred_save_error " + ex.Message);
        }
    }

    private static bool TryReadPendingQuitState(out PendingQuitState state)
    {
        // Legacy or unreadable reservation files must default to "pause not issued"
        // so cancellation never resumes chopping unless this manager requested it.
        state = new PendingQuitState(
            PendingQuitPhase.WaitingForWorkloadStop,
            "",
            "",
            "",
            false,
            DateTimeOffset.MinValue);
        try
        {
            if (!File.Exists(PendingQuitStatePath))
            {
                return false;
            }

            string workloadId = "";
            string instanceId = "";
            string reason = "";
            var pauseUntilIdleRequested = false;
            var phase = PendingQuitPhase.WaitingForWorkloadStop;
            var requestedAt = DateTimeOffset.MinValue;
            foreach (var line in File.ReadAllLines(PendingQuitStatePath, Encoding.UTF8))
            {
                var index = line.IndexOf('=');
                if (index <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, index).Trim();
                var value = line.Substring(index + 1).Trim();
                if (string.Equals(key, "phase", StringComparison.OrdinalIgnoreCase))
                {
                    PendingQuitPhase parsedPhase;
                    if (Enum.TryParse(value, true, out parsedPhase))
                    {
                        phase = parsedPhase;
                    }
                }
                else if (string.Equals(key, "requested_at", StringComparison.OrdinalIgnoreCase))
                {
                    DateTimeOffset parsedRequestedAt;
                    if (DateTimeOffset.TryParse(value, out parsedRequestedAt))
                    {
                        requestedAt = parsedRequestedAt;
                    }
                }
                else if (string.Equals(key, "workload_id", StringComparison.OrdinalIgnoreCase))
                {
                    workloadId = value;
                }
                else if (string.Equals(key, "instance_id", StringComparison.OrdinalIgnoreCase))
                {
                    instanceId = value;
                }
                else if (string.Equals(key, "reason", StringComparison.OrdinalIgnoreCase))
                {
                    reason = value;
                }
                else if (string.Equals(key, "pause_until_idle_requested", StringComparison.OrdinalIgnoreCase))
                {
                    pauseUntilIdleRequested = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                }
            }

            state = new PendingQuitState(
                phase,
                workloadId,
                instanceId,
                reason,
                pauseUntilIdleRequested,
                requestedAt);
            return !string.IsNullOrEmpty(workloadId);
        }
        catch (Exception ex)
        {
            Log("salad_app_quit_deferred_read_error " + ex.Message);
            return false;
        }
    }

    private static void DeletePendingQuitState()
    {
        try
        {
            if (File.Exists(PendingQuitStatePath))
            {
                File.Delete(PendingQuitStatePath);
            }
        }
        catch (Exception ex)
        {
            Log("salad_app_quit_deferred_delete_error " + ex.Message);
        }
    }

    private static void StartPendingQuitLogWatcher(string workloadId, string instanceId)
    {
        StopPendingQuitLogWatcher();
        if (string.IsNullOrEmpty(workloadId))
        {
            Log("salad_app_quit_log_watch_skipped workload_empty");
            return;
        }

        try
        {
            var latest = GetLatestSaladLogFile();
            if (latest == null || latest.Directory == null)
            {
                Log("salad_app_quit_log_watch_skipped log_missing workload=" + workloadId);
                return;
            }

            var watcher = new FileSystemWatcher(latest.Directory.FullName, "log-*.txt");
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
            FileSystemEventHandler handler = delegate { QueuePendingQuitLogEvaluation(); };
            RenamedEventHandler renamedHandler = delegate { QueuePendingQuitLogEvaluation(); };
            watcher.Changed += handler;
            watcher.Created += handler;
            watcher.Renamed += renamedHandler;
            watcher.EnableRaisingEvents = true;
            pendingQuitLogWatcher = watcher;
            Log("salad_app_quit_log_watch_started workload=" + workloadId + " instance=" + instanceId + " dir=" + latest.Directory.FullName);
            QueuePendingQuitLogEvaluation();
        }
        catch (Exception ex)
        {
            Log("salad_app_quit_log_watch_start_error " + ex.Message);
        }
    }

    private static void StopPendingQuitLogWatcher()
    {
        var watcher = pendingQuitLogWatcher;
        pendingQuitLogWatcher = null;
        if (watcher == null)
        {
            return;
        }

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            Log("salad_app_quit_log_watch_stopped");
        }
        catch (Exception ex)
        {
            Log("salad_app_quit_log_watch_stop_error " + ex.Message);
        }
    }

    private static void QueuePendingQuitLogEvaluation()
    {
        var pending = pendingQuitSession.Snapshot();
        if (!pending.IsActive || string.IsNullOrEmpty(pending.WorkloadId))
        {
            return;
        }

        Interlocked.Exchange(ref pendingQuitLogEvaluationRequested, 1);
        if (Interlocked.CompareExchange(ref pendingQuitLogEvaluationRunning, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref pendingQuitLogEvaluationRequested, 0);
                    Thread.Sleep(50);
                    EvaluatePendingQuitFromLog();
                }
                while (Volatile.Read(ref pendingQuitLogEvaluationRequested) != 0 &&
                    pendingQuitSession.Snapshot().IsActive);
            }
            finally
            {
                Interlocked.Exchange(ref pendingQuitLogEvaluationRunning, 0);
                // Close the race where an event arrives after the loop check but before Running is cleared.
                if (Interlocked.Exchange(ref pendingQuitLogEvaluationRequested, 0) != 0 &&
                    pendingQuitSession.Snapshot().IsActive)
                {
                    QueuePendingQuitLogEvaluation();
                }
            }
        });
    }

    private static void EvaluatePendingQuitFromLog()
    {
        var pending = pendingQuitSession.Snapshot();
        if (!pending.IsActive || string.IsNullOrEmpty(pending.WorkloadId) ||
            trayActions.IsRunning(TrayActionDomain.Salad))
        {
            return;
        }

        var decision = GetPendingQuitLogDecision(pending.WorkloadId, pending.InstanceId);
        if (decision != PendingQuitLogDecision.SafeToQuit)
        {
            return;
        }

        if (!pendingQuitSession.TryEnterQuietWindow())
        {
            return;
        }

        Log("salad_app_pending_quit_safe_from_log workload=" + pending.WorkloadId);
        if (!BeginTrayAction(TrayActionDomain.Salad, "quit_salad_app_after_workload_log_idle", delegate
        {
            StopPendingSaladAppProcessesIfStillIdle();
        }))
        {
            pendingQuitSession.ReturnToWaiting();
        }
    }

    private static void StopPendingSaladAppProcessesIfStillIdle()
    {
        if (!IsPendingQuitStillSafe())
        {
            pendingQuitSession.ReturnToWaiting();
            Log("salad_app_pending_quit_final_check_deferred phase=initial");
            return;
        }

        // Workloads can be reassigned immediately after the previous instance
        // stops. Require a short quiet window, then inspect the newest desired
        // state and state block again before sending CloseMainWindow.
        Thread.Sleep(TimeSpan.FromSeconds(2));
        if (!IsPendingQuitStillSafe())
        {
            pendingQuitSession.ReturnToWaiting();
            Log("salad_app_pending_quit_final_check_deferred phase=quiet_window");
            return;
        }

        var pending = pendingQuitSession.Snapshot();
        if (!pendingQuitSession.TryEnterClosing())
        {
            return;
        }

        if (!StopSaladAppProcesses(
            string.IsNullOrEmpty(pending.Reason) ? "salad_app_pending_quit" : pending.Reason,
            true))
        {
            pendingQuitSession.ReturnToWaiting();
        }
    }

    private static bool RequestPauseUntilIdleForSaladExit(string reason)
    {
        var result = CallSaladBowlGrpcHttp2Empty("StopActiveWorkloads");
        Log("salad_app_pause_until_idle_result reason=" + reason + " " + result);
        if (IsGrpcSuccess(result))
        {
            return true;
        }

        SetTrayStatus("Pause until idle failed | " + result, System.Drawing.SystemIcons.Error);
        return false;
    }

    private static bool EnsurePauseUntilIdleBeforeSaladExit(string reason)
    {
        // The process-close boundary must use current log bytes even if the file
        // watcher notification is delayed or coalesced by Windows.
        PollIncrementalWorkloadTracker();
        if (IsLatestSaladRunningStatePaused(GetRecentSaladLogSnapshot()))
        {
            Log("salad_app_pause_until_idle_already_confirmed reason=" + reason);
            return true;
        }

        var requestedAt = DateTimeOffset.Now;
        if (!RequestPauseUntilIdleForSaladExit(reason + "_final"))
        {
            return false;
        }

        var deadline = DateTimeOffset.Now + PauseUntilIdleConfirmationTimeout;
        while (DateTimeOffset.Now < deadline)
        {
            PollIncrementalWorkloadTracker();
            if (HasSaladRunningStateFalseAtOrAfter(GetRecentSaladLogSnapshot(), requestedAt))
            {
                Log("salad_app_pause_until_idle_confirmed reason=" + reason);
                return true;
            }

            Thread.Sleep(250);
        }

        Log("salad_app_pause_until_idle_confirmation_timeout reason=" + reason);
        SetTrayStatus("Pause until idle was not confirmed; Salad remains open", System.Drawing.SystemIcons.Error);
        return false;
    }

    private static bool IsLatestSaladRunningStatePaused(SaladLogSnapshot snapshot)
    {
        bool running;
        return TryGetLatestSaladRunningStateAtOrAfter(snapshot, DateTimeOffset.MinValue, out running) && !running;
    }

    private static bool HasSaladRunningStateFalseAtOrAfter(SaladLogSnapshot snapshot, DateTimeOffset requestedAt)
    {
        bool running;
        return TryGetLatestSaladRunningStateAtOrAfter(
            snapshot,
            requestedAt - TimeSpan.FromSeconds(1),
            out running) && !running;
    }

    private static bool IsPendingQuitStillSafe()
    {
        var pending = pendingQuitSession.Snapshot();
        if (!pending.IsActive || string.IsNullOrEmpty(pending.WorkloadId) || !IsSaladTrayProcessRunning())
        {
            return false;
        }

        if (GetPendingQuitLogDecision(pending.WorkloadId, pending.InstanceId) != PendingQuitLogDecision.SafeToQuit)
        {
            return false;
        }

        var latest = GetRecentSaladWorkloadSnapshot();
        return !IsGpuWorkloadState(latest.State);
    }

    private static PendingQuitLogDecision GetPendingQuitLogDecision(string workloadId, string instanceId)
    {
        try
        {
            var snapshot = GetRecentSaladLogSnapshot();
            if (!snapshot.Available)
            {
                return PendingQuitLogDecision.KeepWaiting;
            }

            return GetPendingQuitLogDecisionFromLines(snapshot.Lines, workloadId, instanceId);
        }
        catch (Exception ex)
        {
            Log("salad_app_quit_log_watch_probe_error " + ex.Message);
            return PendingQuitLogDecision.KeepWaiting;
        }
    }

    private static PendingQuitLogDecision GetPendingQuitLogDecisionFromLines(
        string[] lines,
        string workloadId,
        string instanceId)
    {
        try
        {
            // Production and replay tests intentionally share this pure parser.
            return PendingQuitLogAnalyzer.Evaluate(lines, workloadId, instanceId);
        }
        catch (Exception ex)
        {
            Log("salad_app_quit_log_decision_error " + ex.Message);
            return PendingQuitLogDecision.KeepWaiting;
        }
    }

    private struct PendingQuitState
    {
        public readonly PendingQuitPhase Phase;
        public readonly string WorkloadId;
        public readonly string InstanceId;
        public readonly string Reason;
        public readonly bool PauseUntilIdleRequested;
        public readonly DateTimeOffset RequestedAt;

        public PendingQuitState(
            PendingQuitPhase phase,
            string workloadId,
            string instanceId,
            string reason,
            bool pauseUntilIdleRequested,
            DateTimeOffset requestedAt)
        {
            Phase = phase;
            WorkloadId = workloadId ?? "";
            InstanceId = instanceId ?? "";
            Reason = reason ?? "";
            PauseUntilIdleRequested = pauseUntilIdleRequested;
            RequestedAt = requestedAt;
        }
    }

}
