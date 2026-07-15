using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

internal static class Program
{
    private const string ServiceName = "SaladBowl";

    private static int Main(string[] args)
    {
        var requestId = GetStringArg(args, "--request-id");
        var resultPath = GetStringArg(args, "--result");
        var logPath = GetStringArg(args, "--log");
        if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(resultPath))
        {
            return 2;
        }

        try
        {
            Log(logPath, "repair_start request_id=" + requestId);

            // This helper intentionally owns only the one service operation.
            // It never stops Salad.exe and never invokes wsl.exe.
            var stop = RunSc("stop " + ServiceName, TimeSpan.FromSeconds(20));
            Log(logPath, "service_stop exit=" + stop.ExitCode + " output=" + OneLine(stop.Output) + " error=" + OneLine(stop.Error));
            if (!WaitForServiceState("STOPPED", TimeSpan.FromSeconds(45)))
            {
                var state = QueryServiceState();
                WriteResult(resultPath, requestId, false, "SaladBowl did not stop. Current state: " + state);
                return 3;
            }

            var start = RunSc("start " + ServiceName, TimeSpan.FromSeconds(20));
            Log(logPath, "service_start exit=" + start.ExitCode + " output=" + OneLine(start.Output) + " error=" + OneLine(start.Error));
            if (!WaitForServiceState("RUNNING", TimeSpan.FromSeconds(60)))
            {
                var state = QueryServiceState();
                WriteResult(resultPath, requestId, false, "SaladBowl did not start. Current state: " + state);
                return 4;
            }

            WriteResult(resultPath, requestId, true, "SaladBowl restarted successfully.");
            Log(logPath, "repair_success request_id=" + requestId);
            return 0;
        }
        catch (Exception ex)
        {
            Log(logPath, "repair_error request_id=" + requestId + " error=" + ex);
            WriteResult(resultPath, requestId, false, ex.Message);
            return 5;
        }
    }

    private static bool WaitForServiceState(string expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (string.Equals(QueryServiceState(), expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            Thread.Sleep(1000);
        }

        return string.Equals(QueryServiceState(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string QueryServiceState()
    {
        var result = RunSc("query " + ServiceName, TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0)
        {
            return "UNKNOWN";
        }

        foreach (var rawLine in result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
            {
                return parts[3].Trim();
            }
        }

        return "UNKNOWN";
    }

    private static CommandResult RunSc(string arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo();
        startInfo.FileName = "sc.exe";
        startInfo.Arguments = arguments;
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        using (var process = new Process())
        {
            process.StartInfo = startInfo;
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                }

                return new CommandResult(-1, output, "timeout " + error);
            }

            return new CommandResult(process.ExitCode, output, error);
        }
    }

    private static void WriteResult(string path, string requestId, bool success, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tempPath = path + ".tmp." + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
            File.WriteAllLines(
                tempPath,
                new[]
                {
                    "request_id=" + requestId,
                    "status=" + (success ? "success" : "failure"),
                    "message=" + OneLine(message),
                    "timestamp=" + DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture)
                },
                new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }
        catch
        {
        }
    }

    private static void Log(string path, string message)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.AppendAllText(
                path,
                DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private static string GetStringArg(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return "";
    }

    private static string OneLine(string value)
    {
        return (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private struct CommandResult
    {
        public readonly int ExitCode;
        public readonly string Output;
        public readonly string Error;

        public CommandResult(int exitCode, string output, string error)
        {
            ExitCode = exitCode;
            Output = output ?? "";
            Error = error ?? "";
        }
    }
}
