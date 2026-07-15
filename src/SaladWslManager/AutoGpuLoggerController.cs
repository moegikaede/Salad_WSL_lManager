using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

internal static partial class Program
{
    private static readonly TimeSpan AutoGpuLoggerScanCacheDuration = TimeSpan.FromMinutes(2);

    private static int autoLoggerProcessId;
    private static DateTimeOffset lastAutoGpuLoggerScanAt = DateTimeOffset.MinValue;
    private static bool lastAutoGpuLoggerScanResult;

    private static void StartAutoGpuLogger()
    {
        if (IsAutoGpuLoggerRunning())
        {
            return;
        }

        var distroState = GetDistroState();
        if (!string.Equals(distroState, "RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            Log("auto_gpu_logger_start_skipped distro=" + distroState);
            return;
        }

        var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SaladGpuAutoLogger.ps1");
        if (!File.Exists(scriptPath))
        {
            Log("auto_gpu_logger_missing path=" + scriptPath);
            return;
        }

        var startInfo = new ProcessStartInfo();
        startInfo.FileName = "powershell.exe";
        startInfo.Arguments =
            "-NoProfile -ExecutionPolicy Bypass -File " +
            Quote(scriptPath) +
            " -Distro " +
            Quote(DistroName);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        try
        {
            var process = Process.Start(startInfo);
            autoLoggerProcessId = process == null ? 0 : process.Id;
            SaveAutoGpuLoggerPid(autoLoggerProcessId);
            lastAutoGpuLoggerScanAt = DateTimeOffset.Now;
            lastAutoGpuLoggerScanResult = autoLoggerProcessId > 0;
            Log("auto_gpu_logger_start pid=" + autoLoggerProcessId + " path=" + scriptPath + " distro=" + distroState);
        }
        catch (Exception ex)
        {
            Log("auto_gpu_logger_start_error " + ex.Message);
        }
    }

    private static void StopAutoGpuLogger(string reason)
    {
        var pid = GetKnownAutoGpuLoggerPid();
        if (pid <= 0)
        {
            var now = DateTimeOffset.Now;
            if (lastAutoGpuLoggerScanAt != DateTimeOffset.MinValue &&
                now - lastAutoGpuLoggerScanAt < AutoGpuLoggerScanCacheDuration &&
                !lastAutoGpuLoggerScanResult)
            {
                return;
            }

            pid = FindAutoGpuLoggerPidByCim();
            lastAutoGpuLoggerScanAt = now;
            lastAutoGpuLoggerScanResult = pid > 0;
        }

        if (pid > 0)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    process.Kill();
                    Log("auto_gpu_logger_stop reason=" + reason + " pid=" + pid.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                Log("auto_gpu_logger_stop_pid_error reason=" + reason + " pid=" + pid.ToString(CultureInfo.InvariantCulture) + " error=" + ex.Message);
            }
        }
        else
        {
            return;
        }

        autoLoggerProcessId = 0;
        DeleteAutoGpuLoggerPid();
        lastAutoGpuLoggerScanAt = DateTimeOffset.Now;
        lastAutoGpuLoggerScanResult = false;
    }

    private static bool IsAutoGpuLoggerRunning()
    {
        var pid = GetKnownAutoGpuLoggerPid();
        if (pid > 0)
        {
            return true;
        }

        var now = DateTimeOffset.Now;
        if (lastAutoGpuLoggerScanAt != DateTimeOffset.MinValue &&
            now - lastAutoGpuLoggerScanAt < AutoGpuLoggerScanCacheDuration)
        {
            return lastAutoGpuLoggerScanResult;
        }

        pid = FindAutoGpuLoggerPidByCim();
        lastAutoGpuLoggerScanAt = now;
        lastAutoGpuLoggerScanResult = pid > 0;
        if (pid > 0)
        {
            autoLoggerProcessId = pid;
            SaveAutoGpuLoggerPid(pid);
            return true;
        }

        return false;
    }

    private static int GetKnownAutoGpuLoggerPid()
    {
        if (autoLoggerProcessId <= 0)
        {
            autoLoggerProcessId = ReadAutoGpuLoggerPid();
        }

        if (autoLoggerProcessId <= 0)
        {
            return 0;
        }

        try
        {
            var process = Process.GetProcessById(autoLoggerProcessId);
            if (!process.HasExited)
            {
                return autoLoggerProcessId;
            }
        }
        catch
        {
        }

        autoLoggerProcessId = 0;
        DeleteAutoGpuLoggerPid();
        return 0;
    }

    private static int FindAutoGpuLoggerPidByCim()
    {
        var command =
            "Get-CimInstance Win32_Process | " +
            "Where-Object { $_.CommandLine -match 'SaladGpuAutoLogger\\.ps1' } | " +
            "Select-Object -First 1 -ExpandProperty ProcessId";
        var result = RunProcess(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command " + Quote(command),
            TimeSpan.FromSeconds(10));
        int pid;
        return result.ExitCode == 0 && int.TryParse(OneLine(result.Output), out pid) && pid > 0
            ? pid
            : 0;
    }

    private static void SaveAutoGpuLoggerPid(int pid)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            if (pid <= 0)
            {
                DeleteAutoGpuLoggerPid();
                return;
            }

            File.WriteAllText(
                AutoGpuLoggerPidPath,
                pid.ToString(CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log("auto_gpu_logger_pid_save_error " + ex.Message);
        }
    }

    private static int ReadAutoGpuLoggerPid()
    {
        try
        {
            if (!File.Exists(AutoGpuLoggerPidPath))
            {
                return 0;
            }

            int pid;
            var raw = File.ReadAllText(AutoGpuLoggerPidPath, Encoding.UTF8).Trim();
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) ? pid : 0;
        }
        catch (Exception ex)
        {
            Log("auto_gpu_logger_pid_read_error " + ex.Message);
            return 0;
        }
    }

    private static void DeleteAutoGpuLoggerPid()
    {
        try
        {
            if (File.Exists(AutoGpuLoggerPidPath))
            {
                File.Delete(AutoGpuLoggerPidPath);
            }
        }
        catch (Exception ex)
        {
            Log("auto_gpu_logger_pid_delete_error " + ex.Message);
        }
    }
}
