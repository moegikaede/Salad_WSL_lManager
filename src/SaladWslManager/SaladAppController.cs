using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

internal static partial class Program
{
    private static bool IsSaladTrayProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName("Salad").Any(p =>
            {
                try
                {
                    return !p.HasExited;
                }
                catch
                {
                    return false;
                }
            });
        }
        catch (Exception ex)
        {
            Log("salad_process_probe_error " + ex.Message);
            return true;
        }
    }

    private static void RequestStartSaladApp()
    {
        Log("tray_start_salad_app_clicked");
        StartSaladAppIfMissing("tray_start_salad_app");
    }

    private static void StartSaladAppIfMissing(string reason)
    {
        if (IsSaladTrayProcessRunning())
        {
            Log("salad_app_already_running reason=" + reason);
            SetTrayStatus("Salad app already running", System.Drawing.SystemIcons.Application);
            return;
        }

        var saladExe = ResolveSaladExePath();
        if (string.IsNullOrEmpty(saladExe))
        {
            Log("salad_app_path_not_found");
            SetTrayStatus("Salad app not found", System.Drawing.SystemIcons.Error);
            return;
        }

        try
        {
            var startedAt = DateTimeOffset.Now;
            var startInfo = new ProcessStartInfo
            {
                FileName = saladExe,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(saladExe)
            };
            Process.Start(startInfo);
            Log("salad_app_start reason=" + reason + " path=" + saladExe);
            SetTrayStatus("Salad app starting", System.Drawing.SystemIcons.Application);
            EnsureChopNowAfterSaladStart(startedAt, reason);
        }
        catch (Exception ex)
        {
            Log("salad_app_start_error reason=" + reason + " error=" + ex.Message);
            SetTrayStatus("Salad app start failed", System.Drawing.SystemIcons.Error);
        }
    }

    private static void EnsureChopNowAfterSaladStart(DateTimeOffset startedAt, string reason)
    {
        // SaladBowl can remain reachable while Salad.exe is closed. Wait for the
        // new UI session to publish its running state before changing intent, or
        // that later startup state can overwrite an early Chop-now request.
        var startupDeadline = DateTimeOffset.Now + SaladStartupStateWait;
        bool startupRunning;
        while (DateTimeOffset.Now < startupDeadline)
        {
            if (!IsSaladTrayProcessRunning())
            {
                Log("salad_app_start_chop_aborted process_missing reason=" + reason);
                SetTrayStatus("Salad app exited before Chop now", System.Drawing.SystemIcons.Error);
                return;
            }

            PollIncrementalWorkloadTracker();
            if (TryGetLatestSaladRunningStateAtOrAfter(
                GetRecentSaladLogSnapshot(),
                startedAt - TimeSpan.FromSeconds(1),
                out startupRunning))
            {
                Log("salad_app_start_state_ready running=" + startupRunning + " reason=" + reason);
                if (startupRunning)
                {
                    SetTrayStatus("Salad is chopping", System.Drawing.SystemIcons.Application);
                    return;
                }

                break;
            }

            Thread.Sleep(250);
        }

        var confirmationDeadline = DateTimeOffset.Now + ChopNowConfirmationTimeout;
        var attempt = 0;
        while (DateTimeOffset.Now < confirmationDeadline)
        {
            if (!IsSaladTrayProcessRunning())
            {
                Log("salad_app_start_chop_aborted process_missing reason=" + reason);
                SetTrayStatus("Salad app exited before Chop now", System.Drawing.SystemIcons.Error);
                return;
            }

            attempt++;
            var requestedAt = DateTimeOffset.Now;
            var result = CallSaladBowlGrpcHttp2Empty("StartActiveWorkloads");
            Log("salad_app_start_chop_result attempt=" + attempt + " reason=" + reason + " " + result);

            var attemptDeadline = DateTimeOffset.Now + ChopNowRetryDelay;
            if (attemptDeadline > confirmationDeadline)
            {
                attemptDeadline = confirmationDeadline;
            }

            while (DateTimeOffset.Now < attemptDeadline)
            {
                PollIncrementalWorkloadTracker();
                bool running;
                if (TryGetLatestSaladRunningStateAtOrAfter(
                    GetRecentSaladLogSnapshot(),
                    requestedAt - TimeSpan.FromSeconds(1),
                    out running) && running)
                {
                    Log("salad_app_start_chop_confirmed attempt=" + attempt + " reason=" + reason);
                    SetTrayStatus("Salad is chopping", System.Drawing.SystemIcons.Application);
                    return;
                }

                if (!IsSaladTrayProcessRunning())
                {
                    Log("salad_app_start_chop_aborted process_missing reason=" + reason);
                    SetTrayStatus("Salad app exited before Chop now", System.Drawing.SystemIcons.Error);
                    return;
                }

                Thread.Sleep(250);
            }
        }

        Log("salad_app_start_chop_confirmation_timeout reason=" + reason);
        SetTrayStatus("Chop now was not confirmed", System.Drawing.SystemIcons.Error);
    }

    private static bool TryGetLatestSaladRunningStateAtOrAfter(
        SaladLogSnapshot snapshot,
        DateTimeOffset threshold,
        out bool running)
    {
        running = false;
        if (!snapshot.Available || snapshot.Lines == null)
        {
            return false;
        }

        for (var i = snapshot.Lines.Length - 1; i >= 0; i--)
        {
            var line = snapshot.Lines[i];
            var isTrue = line.IndexOf("Running State: true", StringComparison.OrdinalIgnoreCase) >= 0;
            var isFalse = line.IndexOf("Running State: false", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isTrue && !isFalse)
            {
                continue;
            }

            var at = TryParseSaladLogPrefixLocalTime(line);
            if (!at.HasValue || at.Value < threshold)
            {
                return false;
            }

            running = isTrue;
            return true;
        }

        return false;
    }

    private static void RestartSaladAppForConfigReload(bool isPausedIndefinitely)
    {
        Log("salad_app_restart_skipped observation_only=true paused=" + isPausedIndefinitely);
    }

    private static bool StopSaladAppProcesses(string reason, bool updateStatus)
    {
        var processes = GetSaladProcesses();
        if (processes.Length == 0)
        {
            Log("salad_app_not_running reason=" + reason);
            if (updateStatus)
            {
                SetTrayStatus("Salad app not running", System.Drawing.SystemIcons.Warning);
            }

            pendingQuitSession.Clear();
            StopPendingQuitLogWatcher();
            DeletePendingQuitState();
            return true;
        }

        // Every managed Salad exit must pass through the official Pause-until-idle
        // transition; leaving Chop active can strand Bowl's next startup session.
        if (!EnsurePauseUntilIdleBeforeSaladExit(reason))
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }

            Log("salad_app_quit_aborted pause_until_idle_unconfirmed reason=" + reason);
            return false;
        }

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited && process.CloseMainWindow())
                {
                    Log("salad_app_close_requested reason=" + reason + " pid=" + process.Id);
                }
            }
            catch (Exception ex)
            {
                Log("salad_app_close_error reason=" + reason + " pid=" + SafeProcessId(process) + " error=" + ex.Message);
            }
        }

        Thread.Sleep(3000);

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    Log("salad_app_killed reason=" + reason + " pid=" + process.Id);
                }
            }
            catch (Exception ex)
            {
                Log("salad_app_kill_error reason=" + reason + " pid=" + SafeProcessId(process) + " error=" + ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        targetStarted = false;
        pendingQuitSession.Clear();
        StopPendingQuitLogWatcher();
        DeletePendingQuitState();
        saladPresentSince = null;
        saladAbsentSince = DateTimeOffset.Now;
        traySaladRunning = false;
        if (updateStatus)
        {
            SetTrayStatus("Salad app quit requested", System.Drawing.SystemIcons.Warning);
        }
        return true;
    }

    private static void ShutdownSaladAppRelatedOnManagerExit()
    {
        RequestManagerShutdown("application_run_returned");
        Log("manager_shutdown_salad_related_skipped observation_only=true");
        if (pollTimer != null)
        {
            pollTimer.Stop();
        }
    }

    private static Process[] GetSaladProcesses()
    {
        try
        {
            return Process.GetProcessesByName("Salad");
        }
        catch (Exception ex)
        {
            Log("salad_process_list_error " + ex.Message);
            return new Process[0];
        }
    }

    private static string ResolveSaladExePath()
    {
        foreach (var path in GetSaladExeCandidates())
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string[] GetSaladExeCandidates()
    {
        return new[]
        {
            GetAppPathExe("Salad.exe", Registry.CurrentUser),
            GetAppPathExe("Salad.exe", Registry.LocalMachine),
            GetInstalledSaladExe(Registry.CurrentUser),
            GetInstalledSaladExe(Registry.LocalMachine),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Salad", "Salad.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Salad", "Salad.exe"),
            GetRunningSaladExePath()
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    private static string GetAppPathExe(string fileName, RegistryKey root)
    {
        try
        {
            using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + fileName))
            {
                return NormalizeExecutablePath(key == null ? null : key.GetValue(null) as string);
            }
        }
        catch (Exception ex)
        {
            Log("salad_app_path_registry_error root=" + root.Name + " error=" + ex.Message);
            return null;
        }
    }

    private static string GetInstalledSaladExe(RegistryKey root)
    {
        var uninstallRoots = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var uninstallRoot in uninstallRoots)
        {
            try
            {
                using (var key = root.OpenSubKey(uninstallRoot))
                {
                    if (key == null)
                    {
                        continue;
                    }

                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using (var subKey = key.OpenSubKey(subKeyName))
                        {
                            var displayName = subKey == null ? null : subKey.GetValue("DisplayName") as string;
                            if (displayName == null ||
                                displayName.IndexOf("Salad", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                continue;
                            }

                            var installLocation = subKey.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrWhiteSpace(installLocation))
                            {
                                var candidate = Path.Combine(TrimQuotes(installLocation), "Salad.exe");
                                if (File.Exists(candidate))
                                {
                                    return candidate;
                                }
                            }

                            var displayIcon = NormalizeExecutablePath(subKey.GetValue("DisplayIcon") as string);
                            if (File.Exists(displayIcon))
                            {
                                return displayIcon;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("salad_uninstall_registry_error root=" + root.Name + " key=" + uninstallRoot + " error=" + ex.Message);
            }
        }

        return null;
    }

    private static string GetRunningSaladExePath()
    {
        foreach (var process in GetSaladProcesses())
        {
            try
            {
                if (!process.HasExited && !string.IsNullOrWhiteSpace(process.MainModule.FileName))
                {
                    return process.MainModule.FileName;
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static string NormalizeExecutablePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = TrimQuotes(value.Trim());
        var exeIndex = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            path = path.Substring(0, exeIndex + 4);
        }

        return TrimQuotes(path);
    }

    private static string TrimQuotes(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? value : value.Trim().Trim('"');
    }

    private static int SafeProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsAdministrator()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
