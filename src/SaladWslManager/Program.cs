using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

internal static partial class Program
{
    private const string DistroName = "salad-enterprise-linux";
    private const string ServiceName = "SaladBowl";
    private const string MutexName = "Local\\SaladWslManager";
    private const bool ShutdownAllWslOnStop = true;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusWindowRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartDebounce = TimeSpan.Zero;
    private static readonly TimeSpan StopDebounce = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EnforceStoppedInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ServiceWaitTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LoggerStopGrace = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan EstimateRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EstimateErrorRetryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LoggingHealthyIconGrace = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan ChopNowRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SaladStartupStateWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ChopNowConfirmationTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ChopNowWslWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ChopNowWslStableWait = TimeSpan.FromSeconds(45);
    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SaladWslManager");
    private static readonly string LogPath = Path.Combine(AppDir, "salad-wsl-manager.log");
    private static readonly string UserLogPath = Path.Combine(AppDir, "salad-wsl-manager-user.log");
    private static readonly string EstimatedEarningsCsvPath = Path.Combine(AppDir, "estimated-earnings.csv");
    private static readonly string UserEstimatedEarningsCsvPath = Path.Combine(AppDir, "estimated-earnings-user.csv");
    private static readonly string WorkloadObservationsCsvPath = Path.Combine(AppDir, "workload-observations.csv");
    private static readonly string WorkloadHistoryCsvPath = Path.Combine(AppDir, "workload-history.csv");
    private static readonly string PendingQuitStatePath = Path.Combine(AppDir, "pending-quit-salad-app.txt");
    private static readonly string StatusWindowBoundsPath = Path.Combine(AppDir, "status-window-bounds.txt");
    private static readonly string AutoGpuLoggerPidPath = Path.Combine(AppDir, "auto-gpu-logger.pid");
    private static readonly string ServiceRepairResultPath = Path.Combine(AppDir, "service-repair-result.txt");
    private static readonly string ServiceRepairLogPath = Path.Combine(AppDir, "service-repair.log");

    private static DateTimeOffset? saladPresentSince;
    private static DateTimeOffset? saladAbsentSince;
    private static DateTimeOffset lastStopEnforce = DateTimeOffset.MinValue;
    private static bool targetStarted;
    private static readonly PendingQuitSession pendingQuitSession = new PendingQuitSession();
    private static FileSystemWatcher pendingQuitLogWatcher;
    private static int pendingQuitLogEvaluationRunning;
    private static int pendingQuitLogEvaluationRequested;
    private static DateTimeOffset? lastLoggerWantedAt;
    private static DateTimeOffset lastEstimateRefresh = DateTimeOffset.MinValue;
    private static DateTimeOffset lastWalletRefresh = DateTimeOffset.MinValue;
    private static DateTimeOffset lastWorkloadHistoryRefresh = DateTimeOffset.MinValue;
    private static int walletRefreshRunning;
    private static int estimatedEarningsRefreshRunning;
    private static int workloadHistoryRefreshRunning;
    private static string estimatedNextHourText = "Est/hr ?";
    private static string balanceText = "?";
    private static string last24HoursText = "?";
    private static string pullHealthText = "Pull: OK";
    private static double? lastEstimatedEarningPerFiveMinutes;
    private static double? lastEstimatedEarningPerHour;
    private static double? lastWalletBalance;
    private static double? previousWalletBalance;
    private static DateTimeOffset? lastWalletBalanceAt;
    private static DateTimeOffset? previousWalletBalanceAt;
    private static double? lastWalletBalanceDelta;
    private static double? lastWalletBalanceDeltaSeconds;
    private static double? lastWalletLast24Hours;
    private static NotifyIcon notifyIcon;
    private static ToolStripMenuItem startupRegistrationMenuItem;
    private static ToolStripMenuItem startSaladAppMenuItem;
    private static ToolStripMenuItem quitSaladAppMenuItem;
    private static ToolStripMenuItem chopNowMenuItem;
    private static ToolStripMenuItem pauseUntilIdleMenuItem;
    private static ToolStripMenuItem pauseUntilSetTimeMenuItem;
    private static ToolStripMenuItem pauseIndefinitelyMenuItem;
    private static System.Windows.Forms.Timer pollTimer;
    private static System.Windows.Forms.Timer trayAnimationTimer;
    private static System.Drawing.Icon[] loggingSpinnerIcons;
    private static System.Drawing.Icon[] pullingSpinnerIcons;
    private static System.Drawing.Icon[] stopPendingSpinnerIcons;
    private static System.Drawing.Icon appWindowIcon;
    private static System.Drawing.Icon currentStaticTrayIcon;
    private static System.Drawing.Icon stoppedTrayIcon;
    private static bool traySaladRunning;
    private static Form statusWindow;
    private static Panel statusWindowHeader;
    private static TableLayoutPanel statusWindowTable;
    private static Label[] statusWindowCells;
    private static ProgressBar statusRefreshProgress;
    private static Button saladBowlRepairButton;
    private static ToolTip saladBowlRepairToolTip;
    private static System.Windows.Forms.Timer statusRefreshProgressTimer;
    private static Chart earningsHistoryChart;
    private static Button previousWorkloadButton;
    private static Button nextWorkloadButton;
    private static string selectedWorkloadHistoryKey = "";
    private static Label earningsHistoryRangeLabel;
    private static DateTimeOffset earningsHistoryEnd = DateTimeOffset.MinValue;
    private static DateTimeOffset lastStatusWindowRefreshAt = DateTimeOffset.MinValue;
    private static DateTimeOffset lastStatusWindowChartUpdate = DateTimeOffset.MinValue;
    private static DateTimeOffset lastSuccessfulEarningsLog = DateTimeOffset.MinValue;
    private static int traySpinnerFrame;
    private static bool loggingSpinnerActive;
    private static bool pullingSpinnerActive;
    private static string lastStatus = "Starting";
    private static bool lastAppStateSnapshotAvailable;
    private static AppStateSnapshot lastAppStateSnapshot;
    private static DateTimeOffset lastStatusWindowTrayToggleAt = DateTimeOffset.MinValue;
    private static bool statusWindowWasActive;
    private static DateTimeOffset lastStatusWindowDeactivatedAt = DateTimeOffset.MinValue;
    private static SynchronizationContext uiContext;
    private static int uiThreadId;
    private static readonly TrayActionCoordinator trayActions = new TrayActionCoordinator();
    private static int saladBowlRepairRunning;
    private static bool pendingQuitRestoreChecked;
    private static bool managerShutdownRequested;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static readonly IntPtr HwndTopMost = new IntPtr(-1);
    private static readonly IntPtr HwndNoTopMost = new IntPtr(-2);
    private const int ShowWindowRestore = 9;
    private const uint SetWindowPosNoMove = 0x0002;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosShowWindow = 0x0040;

    [STAThread]
    private static int Main()
    {
        Directory.CreateDirectory(AppDir);
        ConfigureNetworkSecurity();

        bool createdNew;
        using (new Mutex(true, MutexName, out createdNew))
        {
            if (!createdNew)
            {
                return 0;
            }

            Log("manager_start elevated=" + IsAdministrator());
            Log("initial_service_state=" + GetServiceState());
            Log("initial_distro_state=" + GetDistroState());

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            uiContext = SynchronizationContext.Current;
            uiThreadId = Thread.CurrentThread.ManagedThreadId;
            InitializeTrayIcon();
            InitializeIncrementalWorkloadTracker();
            Log("manager_start_observation_only_no_salad_launch");

            pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = (int)PollInterval.TotalMilliseconds;
            pollTimer.Tick += delegate { SafeTick(); };
            pollTimer.Start();

            SafeTick();
            Application.Run();
            StopIncrementalWorkloadTracker();
            ShutdownSaladAppRelatedOnManagerExit();
            CleanupTrayIcon();
            Log("manager_exit");
            return 0;
        }
    }

    private static void SafeTick()
    {
        if (managerShutdownRequested)
        {
            return;
        }

        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            Log("loop_error " + ex);
            SetTrayStatus("Error", System.Drawing.SystemIcons.Error);
        }
    }

    private static void Tick()
    {
        var now = DateTimeOffset.Now;
        PollIncrementalWorkloadTracker();
        var saladRunning = IsSaladTrayProcessRunning();
        traySaladRunning = saladRunning;
        var serviceState = GetServiceState();
        var distroState = GetDistroState();
        var saladLog = GetRecentSaladLogSnapshot();
        var workload = GetRecentSaladWorkloadSnapshot(saladLog);
        // Salad.exe is the user-facing controller. Do not keep reporting an old
        // active workload after that controller has been closed.
        if (!saladRunning)
        {
            workload = new WorkloadSnapshot("", "Paused", "salad_process_missing");
        }
        var workloadState = workload.State;
        RestorePendingQuitIfSameWorkload(workload);
        pullHealthText = GetRecentPullHealthStatus(saladLog);
        UpdatePullingSpinnerState(saladRunning);
        if (statusWindow != null && !statusWindow.IsDisposed && statusWindow.Visible &&
            saladBowlRepairButton != null && !saladBowlRepairButton.IsDisposed)
        {
            // Repair diagnosis scans recent lifecycle blocks, so perform it
            // only while its UI is visible instead of adding permanent polling work.
            UpdateSaladBowlRepairButton(GetSaladBowlRepairDiagnosis(saladLog, saladRunning, serviceState, now));
        }
        UpdateLoggingSpinnerState(saladRunning, workloadState);
        UpdateTrayActionChecks(saladRunning, workloadState);

        var workloadIdText = string.IsNullOrEmpty(workload.DisplayId) ? "?" : workload.DisplayId;
        var workloadTiming = GetRecentWorkloadTimingStatus(saladLog, workload);
        var pendingQuit = pendingQuitSession.Snapshot();
        var displayedWorkloadState = pendingQuit.Phase != PendingQuitPhase.Idle && !string.IsNullOrEmpty(pendingQuit.DisplayPhase)
            ? workloadState + " / Stop: " + pendingQuit.DisplayPhase
            : workloadState;
        var snapshot = new AppStateSnapshot(
            saladRunning ? "Running" : "Missing",
            serviceState,
            distroState,
            displayedWorkloadState,
            workloadIdText,
            pullHealthText,
            workloadTiming.RuntimeText,
            workloadTiming.PastAverageText,
            estimatedNextHourText,
            last24HoursText,
            balanceText);
        SetAppStateStatus(snapshot, saladRunning ? GetApplicationWindowIcon() : System.Drawing.SystemIcons.Warning);

        var loggerWanted = saladRunning &&
            string.Equals(distroState, "RUNNING", StringComparison.OrdinalIgnoreCase) &&
            IsGpuWorkloadState(workloadState);
        var autoLoggerRunning = IsAutoGpuLoggerRunning();
        QueueWalletBalanceRefreshIfNeeded(saladRunning);
        QueueEstimatedEarningsRefreshIfNeeded(saladRunning);
        QueueWorkloadHistoryRefreshIfNeeded(saladLog);

        if (saladRunning)
        {
            saladPresentSince = saladPresentSince ?? now;
            saladAbsentSince = null;

            if (pendingQuitSession.Snapshot().IsActive && !trayActions.IsRunning(TrayActionDomain.Salad))
            {
                // FileSystemWatcher can coalesce notifications, so the normal
                // tick requests reevaluation. Only the instance-aware log
                // decision may launch the close action; using this tick's coarse
                // state caused a failed final check to recursively schedule itself.
                QueuePendingQuitLogEvaluation();
                UpdateTrayAnimationTimer();
            }

            if (!targetStarted && now - saladPresentSince.Value >= StartDebounce)
            {
                targetStarted = true;
                Log("auto_chop_now_skipped observation_only=true");
            }

            if (loggerWanted)
            {
                if (lastLoggerWantedAt == null)
                {
                    Log("auto_gpu_logger_wanted workload=" + workloadState + " distro=" + distroState);
                }

                lastLoggerWantedAt = now;
                if (!autoLoggerRunning)
                {
                    StartAutoGpuLogger();
                    autoLoggerRunning = IsAutoGpuLoggerRunning();
                }
            }
            else if (autoLoggerRunning)
            {
                lastLoggerWantedAt = lastLoggerWantedAt ?? now;
                if (now - lastLoggerWantedAt.Value >= LoggerStopGrace)
                {
                    StopAutoGpuLogger("workload_quiet");
                    lastLoggerWantedAt = null;
                    autoLoggerRunning = IsAutoGpuLoggerRunning();
                }
            }
            else
            {
                lastLoggerWantedAt = null;
            }
        }
        else
        {
            saladAbsentSince = saladAbsentSince ?? now;
            saladPresentSince = null;
            targetStarted = false;
            pendingQuitSession.Clear();
            StopPendingQuitLogWatcher();
            DeletePendingQuitState();
            lastLoggerWantedAt = null;
            StopAutoGpuLogger("salad_missing");
        }

    }

    private static void EnforceStopped()
    {
        Log("enforce_stopped_skipped observation_only=true service=" + GetServiceState() + " distro=" + GetDistroState());
        SetTrayStatus("Stop services disabled in observation mode", System.Drawing.SystemIcons.Warning);
    }

    private static void StopKnownWslProbers()
    {
        var command =
            "Get-CimInstance Win32_Process | " +
            "Where-Object { $_.CommandLine -match 'SaladGpuLogger\\.ps1|SaladGpuAutoLogger\\.ps1' } | " +
            "ForEach-Object { Stop-Process -Id $_.ProcessId -Force; \"stopped_salad_gpu_logger pid=$($_.ProcessId)\" }";
        var result = RunProcess(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command " + Quote(command),
            TimeSpan.FromSeconds(20));
        LogResult("stop_known_wsl_probers", result);
    }

    private static void EnforceStarted()
    {
        Log("enforce_started_skipped observation_only=true service=" + GetServiceState() + " distro=" + GetDistroState());
        SetTrayStatus("Start services disabled in observation mode", System.Drawing.SystemIcons.Warning);
    }

    private static void RequestChopNow()
    {
        Log("tray_chop_now_clicked");
        SetTrayStatus("Chop now request", System.Drawing.SystemIcons.Information);
        Log("chop_now_grpc_begin observation_only=true service=" + GetServiceState(true) + " distro=" + GetDistroState(true));
        var result = CallSaladBowlGrpcHttp2Empty("StartActiveWorkloads");
        Log("chop_now_result observation_only=true " + result + " service=" + GetServiceState(true) + " distro=" + GetDistroState(true));
        UpdateTrayActionChecks(IsSaladTrayProcessRunning(), GetRecentSaladWorkloadState());

        if (IsGrpcSuccess(result))
        {
            SetTrayStatus("Chop now requested | " + result, System.Drawing.SystemIcons.Information);
        }
        else
        {
            SetTrayStatus("Chop now request failed | " + result, System.Drawing.SystemIcons.Error);
        }
    }

    private static void RequestPauseChopping()
    {
        RequestPauseUntilIdle();
    }

    private static void RequestPauseUntilIdle()
    {
        Log("tray_pause_until_idle_clicked");
        SetTrayStatus("Pause until idle disabled in observation mode", System.Drawing.SystemIcons.Warning);
        Log("pause_until_idle_skipped observation_only=true");
    }

    private static void RequestPauseIndefinitely()
    {
        Log("tray_pause_indefinitely_clicked");
        SetTrayStatus("Pause indefinitely disabled in observation mode", System.Drawing.SystemIcons.Warning);
        Log("pause_indefinitely_skipped observation_only=true");
    }

    private static bool IsGrpcSuccess(string result)
    {
        return result != null &&
            result.IndexOf("http=200", StringComparison.OrdinalIgnoreCase) >= 0 &&
            result.IndexOf("grpc=0", StringComparison.OrdinalIgnoreCase) >= 0;
    }












































    private static bool TryGetLatestEstimatedEarningsSampleAt(string path, string machineId, ref DateTimeOffset latest)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var found = false;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                var headerLine = reader.ReadLine();
                if (headerLine == null)
                {
                    return false;
                }

                var headers = ParseCsvLine(headerLine);
                var timeIndex = Array.IndexOf(headers, "captured_at_local");
                var machineIndex = Array.IndexOf(headers, "machine_id");
                if (timeIndex < 0 || machineIndex < 0)
                {
                    return false;
                }

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var fields = ParseCsvLine(line);
                    if (fields.Length <= Math.Max(timeIndex, machineIndex) ||
                        !string.Equals(fields[machineIndex], machineId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    DateTimeOffset capturedAt;
                    if (TryParseDateTimeOffset(fields[timeIndex], out capturedAt) && capturedAt > latest)
                    {
                        latest = capturedAt;
                        found = true;
                    }
                }
            }

            return found;
        }
        catch (Exception ex)
        {
            Log("estimated_earnings_latest_sample_probe_error " + ex.Message);
            return false;
        }
    }




















































    private static string JoinArguments(string[] args)
    {
        return string.Join(" ", args.Select(Quote).ToArray());
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (value.IndexOfAny(new[] { ' ', '\t', '"', '\\', '\r', '\n' }) < 0)
        {
            return value;
        }

        var builder = new StringBuilder();
        builder.Append('"');
        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(ch);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static void LogResult(string action, CommandResult result)
    {
        Log(string.Format(
            "{0} exit={1} stdout={2} stderr={3}",
            action,
            result.ExitCode,
            OneLine(result.Output),
            OneLine(result.Error)));
    }

    private static string OneLine(string value)
    {
        if (value == null)
        {
            return "";
        }

        return value
            .Replace('\0', ' ')
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
    }

    private static void Log(string message)
    {
        if (TryAppendLogLine(LogPath, message))
        {
            return;
        }

        TryAppendLogLine(UserLogPath, message);
    }

    private static bool TryAppendLogLine(string path, string message)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            File.AppendAllText(
                path,
                string.Format("{0:yyyy-MM-ddTHH:mm:ss.fffzzz} {1}{2}", DateTimeOffset.Now, message, Environment.NewLine),
                Encoding.UTF8);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetReadableLogPath()
    {
        if (File.Exists(UserLogPath))
        {
            return UserLogPath;
        }

        return LogPath;
    }

















































}
