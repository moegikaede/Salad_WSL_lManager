using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal static partial class Program
{
    private static void QueueEstimatedEarningsRefreshIfNeeded(bool saladRunning)
    {
        QueueEstimatedEarningsRefreshIfNeeded(saladRunning, EstimateRefreshInterval, false);
    }

    private static void QueueEstimatedEarningsRefreshIfStaleForStatusWindow(bool saladRunning)
    {
        QueueEstimatedEarningsRefreshIfNeeded(
            saladRunning,
            TimeSpan.FromMilliseconds(EstimateRefreshInterval.TotalMilliseconds / 2.0),
            true);
    }

    private static void QueueEstimatedEarningsRefreshIfNeeded(bool saladRunning, TimeSpan staleAfter, bool force)
    {
        if (!saladRunning)
        {
            RefreshEstimatedEarningsIfNeeded(false, false);
            return;
        }

        if (DateTimeOffset.Now - lastEstimateRefresh < staleAfter)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref estimatedEarningsRefreshRunning, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                RefreshEstimatedEarningsIfNeeded(true, force);
            }
            finally
            {
                Interlocked.Exchange(ref estimatedEarningsRefreshRunning, 0);
            }
        });
    }

    private static void RefreshEstimatedEarningsIfNeeded(bool saladRunning)
    {
        RefreshEstimatedEarningsIfNeeded(saladRunning, false);
    }

    private static void RefreshEstimatedEarningsIfNeeded(bool saladRunning, bool force)
    {
        if (!saladRunning)
        {
            estimatedNextHourText = "Est/hr ?";
            lastEstimatedEarningPerFiveMinutes = null;
            lastEstimatedEarningPerHour = null;
            SetLoggingSpinnerActive(false);
            return;
        }

        var now = DateTimeOffset.Now;
        if (!force && now - lastEstimateRefresh < EstimateRefreshInterval)
        {
            return;
        }

        lastEstimateRefresh = now;

        SaladApiCredentials credentials;
        if (!TryReadSaladApiCredentials(out credentials))
        {
            estimatedNextHourText = "Est/hr ?";
            lastEstimatedEarningPerFiveMinutes = null;
            lastEstimatedEarningPerHour = null;
            SetLoggingSpinnerActive(false);
            return;
        }

        try
        {
            var url = "https://app-api.salad.com/api/v2/machines/" + credentials.MachineId + "/earnings/5-minutes";
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = "application/json, text/plain, */*";
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + credentials.Jwt;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var raw = reader.ReadToEnd().Trim().Trim('"');
                double perFiveMinutes;
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out perFiveMinutes))
                {
                    var perHour = perFiveMinutes * 12.0;
                    estimatedNextHourText = "Est/hr $" + perHour.ToString("0.0000", CultureInfo.InvariantCulture);
                    lastEstimatedEarningPerFiveMinutes = perFiveMinutes;
                    lastEstimatedEarningPerHour = perHour;
                    if (ShouldAppendEstimatedEarningsSample(now, credentials.MachineId))
                    {
                        AppendEstimatedEarningsSample(now, credentials.MachineId, perFiveMinutes, perHour, (int)response.StatusCode);
                        AppendWorkloadObservationSample(now, perFiveMinutes, perHour, (int)response.StatusCode);
                    }
                    else
                    {
                        Log("estimated_earnings_log_skipped recent_persisted_sample=true");
                    }

                    lastSuccessfulEarningsLog = now;
                    UpdateLoggingSpinnerState(IsSaladTrayProcessRunning(), GetRecentSaladWorkloadState());
                    Log(
                        "estimated_earnings_refresh ok per_5min=" +
                        perFiveMinutes.ToString("0.########", CultureInfo.InvariantCulture) +
                        " per_hour=" +
                        perHour.ToString("0.########", CultureInfo.InvariantCulture));
                }
                else
                {
                    estimatedNextHourText = "Est/hr ?";
                    lastEstimatedEarningPerFiveMinutes = null;
                    lastEstimatedEarningPerHour = null;
                    SetLoggingSpinnerActive(false);
                    Log("estimated_earnings_parse_error response=" + OneLine(raw));
                }
            }
        }
        catch (Exception ex)
        {
            estimatedNextHourText = "Est/hr ?";
            lastEstimatedEarningPerFiveMinutes = null;
            lastEstimatedEarningPerHour = null;
            SetLoggingSpinnerActive(false);
            lastEstimateRefresh = DateTimeOffset.Now - EstimateRefreshInterval + EstimateErrorRetryInterval;
            Log("estimated_earnings_refresh_error " + ex.Message);
        }
    }

    private static bool ShouldAppendEstimatedEarningsSample(DateTimeOffset capturedAt, string machineId)
    {
        DateTimeOffset latest;
        if (!TryGetLatestEstimatedEarningsSampleAt(machineId, out latest))
        {
            return true;
        }

        return capturedAt - latest >= EstimateRefreshInterval;
    }

    private static bool TryGetLatestEstimatedEarningsSampleAt(string machineId, out DateTimeOffset latest)
    {
        latest = DateTimeOffset.MinValue;
        var found = false;
        found |= TryGetLatestEstimatedEarningsSampleAt(EstimatedEarningsCsvPath, machineId, ref latest);
        found |= TryGetLatestEstimatedEarningsSampleAt(UserEstimatedEarningsCsvPath, machineId, ref latest);
        return found;
    }

    private static void AppendEstimatedEarningsSample(
        DateTimeOffset capturedAt,
        string machineId,
        double perFiveMinutes,
        double perHour,
        int httpStatus)
    {
        if (TryAppendEstimatedEarningsSample(EstimatedEarningsCsvPath, capturedAt, machineId, perFiveMinutes, perHour, httpStatus))
        {
            return;
        }

        TryAppendEstimatedEarningsSample(UserEstimatedEarningsCsvPath, capturedAt, machineId, perFiveMinutes, perHour, httpStatus);
    }

    private static bool TryAppendEstimatedEarningsSample(
        string path,
        DateTimeOffset capturedAt,
        string machineId,
        double perFiveMinutes,
        double perHour,
        int httpStatus)
    {
        try
        {
            var exists = File.Exists(path);
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                if (!exists)
                {
                    writer.WriteLine("captured_at_local,machine_id,earning_usd_per_5min,estimated_usd_per_hour,http_status");
                }

                writer.WriteLine(
                    Csv(capturedAt.ToString("o", CultureInfo.InvariantCulture)) +
                    "," +
                    Csv(machineId) +
                    "," +
                    perFiveMinutes.ToString("0.########", CultureInfo.InvariantCulture) +
                    "," +
                    perHour.ToString("0.########", CultureInfo.InvariantCulture) +
                    "," +
                    httpStatus.ToString(CultureInfo.InvariantCulture));
            }

            return true;
        }
        catch (Exception ex)
        {
            if (string.Equals(path, UserEstimatedEarningsCsvPath, StringComparison.OrdinalIgnoreCase))
            {
                Log("estimated_earnings_log_error " + ex.Message);
            }

            return false;
        }
    }

    private static void AppendWorkloadObservationSample(
        DateTimeOffset capturedAt,
        double perFiveMinutes,
        double perHour,
        int httpStatus)
    {
        try
        {
            var workload = GetRecentSaladWorkloadSnapshot();
            var gpu = GetHostGpuSnapshot();
            var distroState = GetDistroState();
            var saladRunning = IsSaladTrayProcessRunning();
            var confidence = GetAttributionConfidence(workload, gpu, perFiveMinutes);
            var exists = File.Exists(WorkloadObservationsCsvPath);
            using (var stream = new FileStream(WorkloadObservationsCsvPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                if (!exists)
                {
                    writer.WriteLine(
                        "captured_at_local,workload_id,workload_state,workload_source,pull_health,distro_state,salad_running," +
                        "gpu_util_pct,gpu_power_w,gpu_mem_used_mib,earning_usd_per_5min,estimated_usd_per_hour,http_status," +
                        "balance_usd,balance_delta_usd,balance_delta_seconds,last24h_usd,attribution_confidence");
                }

                writer.WriteLine(
                    Csv(capturedAt.ToString("o", CultureInfo.InvariantCulture)) +
                    "," +
                    Csv(workload.Id) +
                    "," +
                    Csv(workload.State) +
                    "," +
                    Csv(workload.Source) +
                    "," +
                    Csv(pullHealthText) +
                    "," +
                    Csv(distroState) +
                    "," +
                    Csv(saladRunning ? "true" : "false") +
                    "," +
                    FormatNullable(gpu.UtilPercent) +
                    "," +
                    FormatNullable(gpu.PowerWatts) +
                    "," +
                    FormatNullable(gpu.MemoryUsedMiB) +
                    "," +
                    perFiveMinutes.ToString("0.########", CultureInfo.InvariantCulture) +
                    "," +
                    perHour.ToString("0.########", CultureInfo.InvariantCulture) +
                    "," +
                    httpStatus.ToString(CultureInfo.InvariantCulture) +
                    "," +
                    FormatNullable(lastWalletBalance) +
                    "," +
                    FormatNullable(lastWalletBalanceDelta) +
                    "," +
                    FormatNullable(lastWalletBalanceDeltaSeconds) +
                    "," +
                    FormatNullable(lastWalletLast24Hours) +
                    "," +
                    Csv(confidence));
            }
        }
        catch (Exception ex)
        {
            Log("workload_observation_log_error " + ex.Message);
        }
    }

    private static string FormatNullable(double? value)
    {
        return value.HasValue ? value.Value.ToString("0.########", CultureInfo.InvariantCulture) : "";
    }

    private static string GetAttributionConfidence(WorkloadSnapshot workload, GpuSnapshot gpu, double perFiveMinutes)
    {
        var hasWorkloadId = !string.IsNullOrEmpty(workload.Id);
        var gpuHigh = gpu.UtilPercent.HasValue && gpu.UtilPercent.Value >= 95.0;
        var balanceMoved = lastWalletBalanceDelta.HasValue && Math.Abs(lastWalletBalanceDelta.Value) > 0.00000001;
        if (hasWorkloadId && gpuHigh && balanceMoved)
        {
            return "high_balance_gpu";
        }

        if (hasWorkloadId && gpuHigh && perFiveMinutes > 0)
        {
            return "high_estimated_gpu";
        }

        if (hasWorkloadId && perFiveMinutes > 0)
        {
            return "medium_estimated";
        }

        if (hasWorkloadId)
        {
            return "workload_only";
        }

        return "unknown";
    }

    private static GpuSnapshot GetHostGpuSnapshot()
    {
        try
        {
            var nvidiaSmi = GetNvidiaSmiPath();
            if (string.IsNullOrEmpty(nvidiaSmi))
            {
                return new GpuSnapshot(null, null, null);
            }

            var result = RunProcess(
                nvidiaSmi,
                "--query-gpu=utilization.gpu,power.draw,memory.used --format=csv,noheader,nounits",
                TimeSpan.FromSeconds(5));
            if (result.ExitCode != 0)
            {
                return new GpuSnapshot(null, null, null);
            }

            var lines = result.Output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                var parts = rawLine.Split(',');
                if (parts.Length < 3)
                {
                    continue;
                }

                double util;
                double power;
                double memory;
                return new GpuSnapshot(
                    TryParseNvidiaNumber(parts[0], out util) ? (double?)util : null,
                    TryParseNvidiaNumber(parts[1], out power) ? (double?)power : null,
                    TryParseNvidiaNumber(parts[2], out memory) ? (double?)memory : null);
            }
        }
        catch (Exception ex)
        {
            Log("gpu_snapshot_error " + ex.Message);
        }

        return new GpuSnapshot(null, null, null);
    }

    private static string Csv(string value)
    {
        if (value == null)
        {
            value = "";
        }

        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static bool TryReadSaladApiCredentials(out SaladApiCredentials credentials)
    {
        credentials = new SaladApiCredentials("", "");

        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Salad",
                "logs",
                "main.log");
            if (!File.Exists(logPath))
            {
                return false;
            }

            string text;
            using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                text = reader.ReadToEnd();
            }

            var machineMatches = Regex.Matches(text, @"machines/([0-9a-fA-F-]{36})/earnings/5-minutes|updateEstimatedEarningsEpic:\s*machineId\s*([0-9a-fA-F-]{36})");
            var tokenMatches = Regex.Matches(text, @"Authorization:\s*'Bearer\s+([^']+)'");
            if (machineMatches.Count == 0 || tokenMatches.Count == 0)
            {
                return false;
            }

            var machineMatch = machineMatches[machineMatches.Count - 1];
            var machineId = machineMatch.Groups[1].Success ? machineMatch.Groups[1].Value : machineMatch.Groups[2].Value;
            var jwt = tokenMatches[tokenMatches.Count - 1].Groups[1].Value;
            if (string.IsNullOrEmpty(machineId) || string.IsNullOrEmpty(jwt))
            {
                return false;
            }

            credentials = new SaladApiCredentials(machineId, jwt);
            return true;
        }
        catch (Exception ex)
        {
            Log("salad_api_credentials_read_error " + ex.Message);
            return false;
        }
    }

    private struct GpuSnapshot
    {
        public readonly double? UtilPercent;
        public readonly double? PowerWatts;
        public readonly double? MemoryUsedMiB;

        public GpuSnapshot(double? utilPercent, double? powerWatts, double? memoryUsedMiB)
        {
            UtilPercent = utilPercent;
            PowerWatts = powerWatts;
            MemoryUsedMiB = memoryUsedMiB;
        }
    }

    private struct SaladApiCredentials
    {
        public readonly string MachineId;
        public readonly string Jwt;

        public SaladApiCredentials(string machineId, string jwt)
        {
            MachineId = machineId;
            Jwt = jwt;
        }
    }
}
