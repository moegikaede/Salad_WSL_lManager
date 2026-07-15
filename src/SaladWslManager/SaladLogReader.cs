using System;
using System.IO;
using System.Linq;
using System.Text;

internal static partial class Program
{
    private static readonly object saladLogSnapshotLock = new object();
    private static SaladLogSnapshot cachedSaladLogSnapshot;

    private static FileInfo GetLatestSaladLogFile()
    {
        return GetRecentSaladLogFiles().FirstOrDefault();
    }

    private static FileInfo[] GetRecentSaladLogFiles()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Salad",
            "logs");
        if (!Directory.Exists(logDir))
        {
            return new FileInfo[0];
        }

        return new DirectoryInfo(logDir)
            .GetFiles("log-*.txt")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(5)
            .ToArray();
    }

    private static SaladLogSnapshot GetRecentSaladLogSnapshot()
    {
        SaladLogSnapshot incremental;
        if (TryGetIncrementalLogSnapshot(out incremental))
        {
            return incremental;
        }

        try
        {
            var candidates = GetRecentSaladLogFiles();
            if (candidates.Length == 0)
            {
                return SaladLogSnapshot.Unavailable("Salad log missing");
            }

            SaladLogSnapshot fallback = SaladLogSnapshot.Unavailable("Salad log missing");
            for (var i = 0; i < candidates.Length; i++)
            {
                var snapshot = ReadSaladLogSnapshot(candidates[i]);
                if (!snapshot.Available)
                {
                    continue;
                }

                if (HasSaladWorkloadStateSignal(snapshot.Lines))
                {
                    if (i > 0)
                    {
                        Log("salad_log_snapshot_fallback file=" + snapshot.FilePath);
                    }

                    return snapshot;
                }

                if (!fallback.Available)
                {
                    fallback = snapshot;
                }
            }

            return fallback.Available ? fallback : SaladLogSnapshot.Unavailable("Salad log missing");
        }
        catch (Exception ex)
        {
            Log("salad_log_snapshot_error " + ex.Message);
            return SaladLogSnapshot.Unavailable("Salad log missing");
        }
    }

    private static SaladLogSnapshot ReadSaladLogSnapshot(FileInfo file)
    {
        var fullName = file.FullName;
        var lastWriteTimeUtc = file.LastWriteTimeUtc;
        string[] lines;
        using (var stream = new FileStream(fullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            // Salad keeps the daily log open. NTFS can expose appended bytes
            // through the stream before FileInfo.Length is refreshed, so the
            // stream length is the authoritative cache key.
            var length = stream.Length;
            lock (saladLogSnapshotLock)
            {
                if (cachedSaladLogSnapshot.Available &&
                    string.Equals(cachedSaladLogSnapshot.FilePath, fullName, StringComparison.OrdinalIgnoreCase) &&
                    cachedSaladLogSnapshot.Length == length)
                {
                    return cachedSaladLogSnapshot;
                }
            }

            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                lines = reader.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            }

            var snapshot = new SaladLogSnapshot(true, "Salad log missing", fullName, lastWriteTimeUtc, length, lines);
            lock (saladLogSnapshotLock)
            {
                cachedSaladLogSnapshot = snapshot;
            }

            return snapshot;
        }
    }

    private static bool HasSaladWorkloadStateSignal(string[] lines)
    {
        if (lines == null)
        {
            return false;
        }

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.IndexOf("Workload Instance States", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("Received desired state from matrix", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("Running State:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("0 workloads", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private struct SaladLogSnapshot
    {
        public readonly bool Available;
        public readonly string UnavailableState;
        public readonly string FilePath;
        public readonly DateTime LastWriteTimeUtc;
        public readonly long Length;
        public readonly string[] Lines;

        public SaladLogSnapshot(
            bool available,
            string unavailableState,
            string filePath,
            DateTime lastWriteTimeUtc,
            long length,
            string[] lines)
        {
            Available = available;
            UnavailableState = string.IsNullOrEmpty(unavailableState) ? "Salad log missing" : unavailableState;
            FilePath = filePath ?? "";
            LastWriteTimeUtc = lastWriteTimeUtc;
            Length = length;
            Lines = lines ?? new string[0];
        }

        public static SaladLogSnapshot Unavailable(string state)
        {
            return new SaladLogSnapshot(false, state, "", DateTime.MinValue, 0, new string[0]);
        }
    }
}
