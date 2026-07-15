using System;
using System.Diagnostics;
using System.Text;

internal static partial class Program
{
    private static CommandResult RunProcess(string fileName, string arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo();
        startInfo.FileName = fileName;
        startInfo.Arguments = arguments;
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        using (var process = new Process())
        {
            process.StartInfo = startInfo;
            var output = new StringBuilder();
            var error = new StringBuilder();
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
            {
                if (args.Data != null)
                {
                    output.AppendLine(args.Data);
                }
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
            {
                if (args.Data != null)
                {
                    error.AppendLine(args.Data);
                }
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }

                    return new CommandResult(-2, output.ToString(), "timeout " + error);
                }

                process.WaitForExit();
                return new CommandResult(process.ExitCode, output.ToString(), error.ToString());
            }
            catch (Exception ex)
            {
                return new CommandResult(-1, output.ToString(), ex.Message + " " + error);
            }
        }
    }

    private struct CommandResult
    {
        public readonly int ExitCode;
        public readonly string Output;
        public readonly string Error;

        public CommandResult(int exitCode, string output, string error)
        {
            ExitCode = exitCode;
            Output = output;
            Error = error;
        }
    }
}
