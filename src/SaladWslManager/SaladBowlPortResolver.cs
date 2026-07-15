using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class Program
{
    private static int GetSaladBowlGrpcPort()
    {
        int logPort;
        if (TryReadSaladBowlGrpcPortFromLog(out logPort))
        {
            return logPort;
        }

        try
        {
            using (var pipe = new NamedPipeClientStream(".", "salad-port", PipeDirection.InOut))
            {
                pipe.Connect(10000);
                TrySetPipeTimeouts(pipe, 10000);

                var request = Encoding.ASCII.GetBytes("?ports\n");
                pipe.Write(request, 0, request.Length);
                pipe.Flush();

                var response = ReadPipeResponse(pipe);
                var match = Regex.Match(response, @"\+(\d+):(\d+)");
                if (!match.Success)
                {
                    throw new IOException("invalid salad-port response " + OneLine(response));
                }

                var grpcWebPort = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var grpcPort = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                if (grpcWebPort < 1 || grpcWebPort > 65535 || grpcPort < 1 || grpcPort > 65535)
                {
                    throw new IOException("invalid salad-port numbers " + OneLine(response));
                }

                Log("salad_bowl_ports grpc=" + grpcPort + " grpc_web=" + grpcWebPort);
                return grpcPort;
            }
        }
        catch (Exception ex)
        {
            Log("salad_port_pipe_error " + ex.Message);
            throw;
        }
    }

    private static bool TryReadSaladBowlGrpcPortFromLog(out int grpcPort)
    {
        grpcPort = 0;
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

            var matches = Regex.Matches(text, @"Found Salad Bowl port numbers\s+(\d+)\s+\(gRPC\)\s+and\s+(\d+)\s+\(gRPC-Web\)");
            if (matches.Count == 0)
            {
                return false;
            }

            var match = matches[matches.Count - 1];
            var parsed = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (parsed < 1 || parsed > 65535)
            {
                return false;
            }

            grpcPort = parsed;
            Log("salad_bowl_ports_from_log grpc=" + grpcPort + " grpc_web=" + match.Groups[2].Value);
            return true;
        }
        catch (Exception ex)
        {
            Log("salad_port_log_error " + ex.Message);
            return false;
        }
    }

    private static void TrySetPipeTimeouts(PipeStream pipe, int milliseconds)
    {
        try
        {
            pipe.ReadTimeout = milliseconds;
            pipe.WriteTimeout = milliseconds;
        }
        catch
        {
        }
    }

    private static string ReadPipeChunk(Stream stream)
    {
        var buffer = new byte[128];
        var read = stream.Read(buffer, 0, buffer.Length);
        if (read <= 0)
        {
            return null;
        }

        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static string ReadPipeResponse(Stream stream)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 10; i++)
        {
            var chunk = ReadPipeChunk(stream);
            if (chunk == null)
            {
                break;
            }

            builder.Append(chunk);
            if (Regex.IsMatch(builder.ToString(), @"\+\d+:\d+"))
            {
                return builder.ToString();
            }
        }

        return builder.ToString();
    }
}
