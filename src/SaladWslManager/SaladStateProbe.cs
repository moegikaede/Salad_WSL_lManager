using System;
using System.Threading;

internal static partial class Program
{
    private static readonly TimeSpan ServiceStateCacheDuration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DistroStateCacheDuration = TimeSpan.FromSeconds(20);

    private static string cachedServiceState;
    private static DateTimeOffset cachedServiceStateAt = DateTimeOffset.MinValue;
    private static string cachedDistroState;
    private static DateTimeOffset cachedDistroStateAt = DateTimeOffset.MinValue;

    private static bool WaitForDistroState(string expectedState, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (string.Equals(GetDistroState(true), expectedState, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            Thread.Sleep(1000);
        }

        return string.Equals(GetDistroState(true), expectedState, StringComparison.OrdinalIgnoreCase);
    }

    private static bool WaitForDistroStableState(string expectedState, TimeSpan startTimeout, TimeSpan stableTimeout)
    {
        if (!WaitForDistroState(expectedState, startTimeout))
        {
            return false;
        }

        var stableUntil = DateTimeOffset.Now + stableTimeout;
        while (DateTimeOffset.Now < stableUntil)
        {
            var state = GetDistroState(true);
            if (!string.Equals(state, expectedState, StringComparison.OrdinalIgnoreCase))
            {
                Log("distro_stability_lost expected=" + expectedState + " actual=" + state);
                return false;
            }

            Thread.Sleep(2000);
        }

        return true;
    }

    private static string GetServiceState()
    {
        return GetServiceState(false);
    }

    private static string GetServiceState(bool forceRefresh)
    {
        var now = DateTimeOffset.Now;
        if (!forceRefresh &&
            cachedServiceStateAt != DateTimeOffset.MinValue &&
            now - cachedServiceStateAt < ServiceStateCacheDuration &&
            !string.IsNullOrEmpty(cachedServiceState))
        {
            return cachedServiceState;
        }

        var state = ProbeServiceState();
        cachedServiceState = state;
        cachedServiceStateAt = now;
        return state;
    }

    private static string ProbeServiceState()
    {
        var result = RunProcess("sc.exe", "query " + ServiceName, TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0)
        {
            return "NOT_FOUND";
        }

        foreach (var rawLine in result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    return parts[3].Trim();
                }
            }
        }

        return "UNKNOWN";
    }

    private static string GetDistroState()
    {
        return GetDistroState(false);
    }

    private static string GetDistroState(bool forceRefresh)
    {
        var now = DateTimeOffset.Now;
        if (!forceRefresh &&
            cachedDistroStateAt != DateTimeOffset.MinValue &&
            now - cachedDistroStateAt < DistroStateCacheDuration &&
            !string.IsNullOrEmpty(cachedDistroState))
        {
            return cachedDistroState;
        }

        var state = ProbeDistroState();
        cachedDistroState = state;
        cachedDistroStateAt = now;
        return state;
    }

    private static string ProbeDistroState()
    {
        var result = RunProcess("wsl.exe", "--list --verbose", TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0)
        {
            return "UNKNOWN";
        }

        foreach (var rawLine in result.Output.Replace("\0", "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.IndexOf(DistroName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (line.IndexOf("Running", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "RUNNING";
                }

                if (line.IndexOf("Stopped", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "STOPPED";
                }

                return line;
            }
        }

        return "NOT_FOUND";
    }

    private static void WaitForServiceState(string expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            var state = GetServiceState(true);
            if (string.Equals(state, expected, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Thread.Sleep(TimeSpan.FromSeconds(2));
        }

        Log("service_wait_timeout expected=" + expected + " actual=" + GetServiceState(true));
    }

    private static void RunSc(params string[] args)
    {
        Log("sc_skipped observation_only=true args=" + string.Join(" ", args));
    }
}
