using System;
using System.Globalization;
using System.IO;
using System.Linq;

internal static partial class Program
{
    // Public builds keep NVIDIA access strictly read-only so GPU/earnings
    // correlation remains available without exposing hardware-setting paths.
    private static bool TryParseNvidiaNumber(string text, out double value)
    {
        value = 0;
        return text != null &&
            double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string GetNvidiaSmiPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var candidates = path.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => Path.Combine(item.Trim(), "nvidia-smi.exe"))
            .ToList();
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"));

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return "";
    }
}
