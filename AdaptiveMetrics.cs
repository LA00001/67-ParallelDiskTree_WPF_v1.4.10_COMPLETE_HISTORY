using System.Globalization;
using System.IO;
using System.Text;

namespace ParallelDiskTree;

public sealed record AdaptiveSample(
    DateTimeOffset Timestamp, double ElapsedSeconds, int Workers, int Active, int Backlog,
    double ObjectsPerSecond, double? DiskBusyPercent, double? DiskLatencyMs,
    double? DiskBytesPerSecond, string Decision)
{
    public string TimeText => ElapsedSeconds.ToString("F1", CultureInfo.CurrentCulture) + " с";
    public string WorkersText => Workers.ToString(CultureInfo.CurrentCulture);
    public string ObjectsText => ObjectsPerSecond.ToString("N0", CultureInfo.CurrentCulture);
    public string BusyText => DiskBusyPercent.HasValue ? DiskBusyPercent.Value.ToString("N1", CultureInfo.CurrentCulture) + "%" : "н/д";
    public string LatencyText => DiskLatencyMs.HasValue ? DiskLatencyMs.Value.ToString("N2", CultureInfo.CurrentCulture) : "н/д";
    public string ThroughputText => DiskBytesPerSecond.HasValue
        ? (DiskBytesPerSecond.Value / (1024 * 1024)).ToString("N1", CultureInfo.CurrentCulture) : "н/д";
}

public static class AdaptiveMetricsStore
{
    public static async Task<string> SaveAsync(string sessionId, string root,
        IReadOnlyList<AdaptiveSample> samples)
    {
        Directory.CreateDirectory(BenchmarkStore.Folder);
        string file = Path.Combine(BenchmarkStore.Folder, "adaptive_samples_v1_4.csv");
        var sb = new StringBuilder();
        if (!File.Exists(file))
            sb.AppendLine("session;root;timestamp_local;elapsed_seconds;target_workers;active_workers;backlog;objects_per_sec;logical_disk_time_percent;avg_disk_latency_ms;disk_bytes_per_sec;decision");
        foreach (var s in samples)
        {
            sb.Append(Csv(sessionId)).Append(';').Append(Csv(root)).Append(';')
                .Append(Csv(s.Timestamp.ToString("O"))).Append(';')
                .Append(Num(s.ElapsedSeconds)).Append(';')
                .Append(s.Workers).Append(';').Append(s.Active).Append(';').Append(s.Backlog).Append(';')
                .Append(Num(s.ObjectsPerSecond)).Append(';')
                .Append(Num(s.DiskBusyPercent)).Append(';').Append(Num(s.DiskLatencyMs)).Append(';')
                .Append(Num(s.DiskBytesPerSecond)).Append(';').Append(Csv(s.Decision)).AppendLine();
        }
        // Single write after stopwatch has stopped; no file writes in the scan hot path.
        await File.AppendAllTextAsync(file, sb.ToString(), new UTF8Encoding(false));
        return file;
    }

    private static string Num(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "";
    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"")
        .Replace('\r', ' ').Replace('\n', ' ') + "\"";
}
