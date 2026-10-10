using System.Globalization;
using System.IO;
using System.Text;

namespace ParallelDiskTree;

public sealed record BenchmarkResult(
    string SessionId, string RootPath, string Mode, int Workers,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt, TimeSpan Elapsed,
    long Files, long Folders, long Bytes, long Errors, string Status,
    int Repeat = 1, int Order = 1,
    double ProcessCpuSeconds = 0, double ProcessCpuPercent = 0,
    double DirectoryWorkerSeconds = 0, double QueueWaitSeconds = 0,
    double IdleWorkerSeconds = 0, long QueueSamples = 0)
{
    public string StartedText => StartedAt.ToString("dd.MM.yyyy HH:mm:ss.fff zzz", CultureInfo.CurrentCulture);
    public string FinishedText => FinishedAt.ToString("dd.MM.yyyy HH:mm:ss.fff zzz", CultureInfo.CurrentCulture);
    public string ElapsedText => Elapsed.TotalSeconds.ToString("N3", CultureInfo.CurrentCulture) + " с";
    public string FilesText => Files.ToString("N0", CultureInfo.CurrentCulture);
    public string FoldersText => Folders.ToString("N0", CultureInfo.CurrentCulture);
    public string BytesText => Bytes.ToString("N0", CultureInfo.CurrentCulture);
    public string SpeedText => (Elapsed.TotalSeconds > 0 ? (Files + Folders) / Elapsed.TotalSeconds : 0)
        .ToString("N0", CultureInfo.CurrentCulture);
    public string QueueWaitText => QueueSamples == 0 ? "—" :
        (QueueWaitSeconds * 1000 / QueueSamples).ToString("N2", CultureInfo.CurrentCulture);
    public string WorkText => DirectoryWorkerSeconds.ToString("N2", CultureInfo.CurrentCulture);
    public string IdleText => Mode == "Последовательный" ? "—" : IdleWorkerSeconds.ToString("N2", CultureInfo.CurrentCulture);
    public string CpuText => ProcessCpuPercent.ToString("N1", CultureInfo.CurrentCulture) + "%";
}

public sealed record ScanSpec(bool Sequential, int Workers, int Repeat, int Order)
{
    public string Mode => Sequential ? "Последовательный" : "Параллельный";
}

public sealed record BenchmarkSummary(
    string Mode, int Workers, int Runs, double MedianSeconds, double BestSeconds,
    double WorstSeconds, double Speedup, long MinFiles, long MaxFiles,
    long MinFolders, long MaxFolders, long MinBytes, long MaxBytes,
    double MeanCpuPercent, double MeanQueueWaitMs, double MeanDirectoryWorkerSeconds,
    double MeanIdleWorkerSeconds, bool Consistent)
{
    public string RunsText => Runs.ToString(CultureInfo.CurrentCulture);
    public string MedianText => MedianSeconds.ToString("N3", CultureInfo.CurrentCulture) + " с";
    public string BestText => BestSeconds.ToString("N3", CultureInfo.CurrentCulture) + " с";
    public string WorstText => WorstSeconds.ToString("N3", CultureInfo.CurrentCulture) + " с";
    public string SpeedupText => Speedup <= 0 ? "—" : Speedup.ToString("N2", CultureInfo.CurrentCulture) + "×";
    public string MatchText => Consistent ? "=" : "Δ";
    public string CpuText => MeanCpuPercent.ToString("N1", CultureInfo.CurrentCulture) + "%";
    public string WaitText => Mode == "Последовательный" ? "—" : MeanQueueWaitMs.ToString("N2", CultureInfo.CurrentCulture);
    public string DirectoryText => MeanDirectoryWorkerSeconds.ToString("N2", CultureInfo.CurrentCulture);
    public string IdleText => Mode == "Последовательный" ? "—" : MeanIdleWorkerSeconds.ToString("N2", CultureInfo.CurrentCulture);
}

public static class BenchmarkAnalyzer
{
    public static IReadOnlyList<ScanSpec> BuildSchedule(IReadOnlyList<int> workerLevels, int repeats)
    {
        if (repeats is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(repeats));
        if (workerLevels.Count == 0 || workerLevels.Count > 12 ||
            workerLevels.Any(x => x is < 1 or > 64) || workerLevels.Distinct().Count() != workerLevels.Count)
            throw new ArgumentException("Нужны уникальные уровни 1..64 (не более 12).", nameof(workerLevels));

        var levels = workerLevels.OrderBy(x => x).ToArray();
        var all = new List<ScanSpec>();
        for (int round = 0; round < repeats; round++)
        {
            // Alternate ascending/descending order. For additional rounds, rotate
            // the list to avoid always giving the same mode the first/warm slot.
            var ordered = round % 2 == 0 ? levels.ToArray() : levels.Reverse().ToArray();
            if (round >= 2)
            {
                int rotation = (round * Math.Max(1, levels.Length / 2)) % levels.Length;
                ordered = ordered.Skip(rotation).Concat(ordered.Take(rotation)).ToArray();
            }
            foreach (int n in ordered)
                all.Add(new ScanSpec(n == 1, n, round + 1, all.Count + 1));
        }
        return all;
    }

    public static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(x => x).ToArray();
        if (ordered.Length == 0) return 0;
        int middle = ordered.Length / 2;
        return ordered.Length % 2 != 0 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    public static IReadOnlyList<BenchmarkSummary> Summarize(IEnumerable<BenchmarkResult> source)
    {
        // Incomplete/cancelled/error scans must not influence speed rankings.
        var complete = source.Where(x => x.Status == "Завершено" && x.Errors == 0).ToArray();
        var baseline = complete.Where(x => x.Mode == "Последовательный" && x.Workers == 1).ToArray();
        double baseMedian = Median(baseline.Select(x => x.Elapsed.TotalSeconds));
        var groups = complete.GroupBy(x => (x.Mode, x.Workers))
            .OrderBy(g => g.Key.Mode == "Последовательный" ? 0 : 1).ThenBy(g => g.Key.Workers);
        long minFiles = complete.Length > 0 ? complete.Min(x => x.Files) : 0;
        long maxFiles = complete.Length > 0 ? complete.Max(x => x.Files) : 0;
        long minFolders = complete.Length > 0 ? complete.Min(x => x.Folders) : 0;
        long maxFolders = complete.Length > 0 ? complete.Max(x => x.Folders) : 0;
        long minBytes = complete.Length > 0 ? complete.Min(x => x.Bytes) : 0;
        long maxBytes = complete.Length > 0 ? complete.Max(x => x.Bytes) : 0;
        bool consistent = minFiles == maxFiles && minFolders == maxFolders && minBytes == maxBytes;
        return groups.Select(g =>
        {
            var rows = g.ToArray();
            double median = Median(rows.Select(x => x.Elapsed.TotalSeconds));
            return new BenchmarkSummary(g.Key.Mode, g.Key.Workers, rows.Length,
                median, rows.Min(x => x.Elapsed.TotalSeconds), rows.Max(x => x.Elapsed.TotalSeconds),
                baseMedian > 0 && median > 0 ? baseMedian / median : 0,
                rows.Min(x => x.Files), rows.Max(x => x.Files),
                rows.Min(x => x.Folders), rows.Max(x => x.Folders),
                rows.Min(x => x.Bytes), rows.Max(x => x.Bytes),
                rows.Average(x => x.ProcessCpuPercent),
                rows.Average(x => x.QueueSamples == 0 ? 0 : x.QueueWaitSeconds * 1000 / x.QueueSamples),
                rows.Average(x => x.DirectoryWorkerSeconds),
                rows.Average(x => x.IdleWorkerSeconds), consistent);
        }).ToArray();
    }

    public static string Explain(IReadOnlyList<BenchmarkResult> results, IReadOnlyList<BenchmarkSummary> summaries,
        int expectedRuns)
    {
        int complete = results.Count(x => x.Status == "Завершено" && x.Errors == 0);
        if (results.Count == 0) return "Замеров пока нет.";
        if (complete == 0) return "Нет полных проходов без ошибок: сравнивать скорость нельзя.";
        bool totalsMatch = results.Where(x => x.Status == "Завершено").Select(x => (x.Files, x.Folders, x.Bytes))
            .Distinct().Count() <= 1;
        var best = summaries.OrderBy(x => x.MedianSeconds).FirstOrDefault();
        string result = $"Полных проходов без ошибок {complete}/{expectedRuns}. " +
            (best is null ? "" : $"Минимальная медиана: {best.Mode} ({best.Workers}), {best.MedianSeconds:N2} с; ускорение {best.Speedup:N2}× к последовательному режиму. ");
        if (!totalsMatch) result += "⚠ Число файлов, папок или байтов изменилось между проходами: дерево не является стабильным снимком. ";
        if (results.Any(x => x.Errors != 0)) result += "⚠ В части проходов были ошибки. ";
        if (results.Count < expectedRuns) result += "Серия не завершена. ";
        return result + "Кэш Windows и фоновая активность влияют на замеры. CPU% — средняя загрузка процесса относительно всех логических процессоров.";
    }
}

public static class BenchmarkStore
{
    // Writes only outside of the timed scanner. v1.2 uses its OWN CSV headers:
    // never mix new columns with the v1.1 benchmarks.csv schema.
    // Portable data: next to the running executable (bin/.../logs under Visual Studio).
    // Do not read or write %LOCALAPPDATA% or user-specific absolute paths.
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "logs");

    public static async Task SaveAsync(IReadOnlyList<BenchmarkResult> results, string? comparison,
        IReadOnlyList<BenchmarkSummary>? summaries = null)
    {
        if (results.Count == 0) return;
        Directory.CreateDirectory(Folder);
        string csvFile = Path.Combine(Folder, "benchmarks_v1_2.csv");
        string summaryFile = Path.Combine(Folder, "median_summary_v1_2.csv");
        string logFile = Path.Combine(Folder, "events.log");
        var csv = new StringBuilder();
        if (!File.Exists(csvFile))
            csv.AppendLine("session;root;mode;workers;repeat;order;start_local;finish_local;duration_ms;files;folders;bytes;errors;objects_per_sec;cpu_process_seconds;cpu_percent_all_logical_cores;directory_worker_seconds;queue_wait_total_seconds;queue_wait_samples;queue_wait_average_ms;worker_idle_total_seconds;status");
        foreach (var r in results)
        {
            csv.Append(Escape(r.SessionId)).Append(';').Append(Escape(r.RootPath)).Append(';')
                .Append(Escape(r.Mode)).Append(';').Append(r.Workers).Append(';').Append(r.Repeat).Append(';')
                .Append(r.Order).Append(';').Append(Escape(r.StartedAt.ToString("O"))).Append(';')
                .Append(Escape(r.FinishedAt.ToString("O"))).Append(';')
                .Append(Invariant(r.Elapsed.TotalMilliseconds)).Append(';')
                .Append(r.Files).Append(';').Append(r.Folders).Append(';').Append(r.Bytes).Append(';')
                .Append(r.Errors).Append(';')
                .Append(Invariant(r.Elapsed.TotalSeconds > 0 ? (r.Files + r.Folders) / r.Elapsed.TotalSeconds : 0)).Append(';')
                .Append(Invariant(r.ProcessCpuSeconds)).Append(';').Append(Invariant(r.ProcessCpuPercent)).Append(';')
                .Append(Invariant(r.DirectoryWorkerSeconds)).Append(';').Append(Invariant(r.QueueWaitSeconds)).Append(';')
                .Append(r.QueueSamples).Append(';').Append(Invariant(r.QueueSamples > 0 ? r.QueueWaitSeconds * 1000 / r.QueueSamples : 0)).Append(';')
                .Append(Invariant(r.IdleWorkerSeconds)).Append(';').Append(Escape(r.Status)).AppendLine();
        }
        await File.AppendAllTextAsync(csvFile, csv.ToString(), new UTF8Encoding(false));

        if (summaries is { Count: > 0 })
        {
            var sb = new StringBuilder();
            if (!File.Exists(summaryFile))
                sb.AppendLine("session;mode;workers;complete_runs;median_seconds;best_seconds;worst_seconds;speedup;min_files;max_files;min_folders;max_folders;min_bytes;max_bytes;cpu_avg_percent;queue_avg_wait_ms;directory_avg_worker_seconds;idle_avg_worker_seconds;all_counts_consistent");
            foreach (var s in summaries)
            {
                sb.Append(Escape(results[0].SessionId)).Append(';').Append(Escape(s.Mode)).Append(';')
                    .Append(s.Workers).Append(';').Append(s.Runs).Append(';')
                    .Append(Invariant(s.MedianSeconds)).Append(';').Append(Invariant(s.BestSeconds)).Append(';')
                    .Append(Invariant(s.WorstSeconds)).Append(';').Append(Invariant(s.Speedup)).Append(';')
                    .Append(s.MinFiles).Append(';').Append(s.MaxFiles).Append(';')
                    .Append(s.MinFolders).Append(';').Append(s.MaxFolders).Append(';')
                    .Append(s.MinBytes).Append(';').Append(s.MaxBytes).Append(';')
                    .Append(Invariant(s.MeanCpuPercent)).Append(';').Append(Invariant(s.MeanQueueWaitMs)).Append(';')
                    .Append(Invariant(s.MeanDirectoryWorkerSeconds)).Append(';').Append(Invariant(s.MeanIdleWorkerSeconds)).Append(';')
                    .Append(s.Consistent ? "true" : "false").AppendLine();
            }
            await File.AppendAllTextAsync(summaryFile, sb.ToString(), new UTF8Encoding(false));
        }

        if (File.Exists(logFile) && new FileInfo(logFile).Length > 1024 * 1024)
            File.Move(logFile, logFile + ".1", overwrite: true);
        var lines = new StringBuilder();
        foreach (var r in results)
            lines.Append(r.FinishedAt.ToString("O")).Append(" | ").Append(r.SessionId)
                .Append(" | ").Append(r.Mode).Append(" | workers=").Append(r.Workers)
                .Append(" | repeat=").Append(r.Repeat).Append(" | seconds=").Append(Invariant(r.Elapsed.TotalSeconds))
                .Append(" | files=").Append(r.Files).Append(" | folders=").Append(r.Folders)
                .Append(" | bytes=").Append(r.Bytes).Append(" | errors=").Append(r.Errors)
                .Append(" | status=").Append(r.Status).AppendLine();
        if (!string.IsNullOrWhiteSpace(comparison))
            lines.Append(DateTimeOffset.Now.ToString("O")).Append(" | ")
                .Append(results[0].SessionId).Append(" | SUMMARY | ")
                .AppendLine(comparison.Replace('\r', ' ').Replace('\n', ' '));
        await File.AppendAllTextAsync(logFile, lines.ToString(), new UTF8Encoding(false));
    }

    private static string Invariant(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
    private static string Escape(string value) => "\"" + value.Replace("\"", "\"\"")
        .Replace("\r", " ").Replace("\n", " ") + "\"";
}
