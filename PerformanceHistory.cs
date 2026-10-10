using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace ParallelDiskTree;

/// <summary>A single complete, zero-error measured pass, with optional diagnostics.</summary>
internal sealed record MeasuredPass(
    string Group, int Workers, double Milliseconds,
    long Files, long Folders, long? Bytes, DateTimeOffset Finished,
    string Source, double? Cpu, double? Queue, double? Directory, double? Idle)
{
    public string Key => Group + ":" + Workers.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Record selected from measured evidence. Missing diagnostics are not estimated.</summary>
public sealed record HistoricalRun(
    int Workers, double Seconds, long Files, long Folders, long? Bytes,
    string Timestamp, string Source, bool IsAdaptive = false,
    bool IsAdaptive64 = false, bool IsBaseline = false, int Runs = 1,
    double? CpuPercent = null, double? QueueAverageMs = null,
    double? DirectoryWorkerSeconds = null, double? WorkerIdleSeconds = null)
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    public string ModeLabel => IsBaseline ? "Эталон • последовательный (1)" :
        IsAdaptive64 ? "🔥🔥64 Адаптивный" : IsAdaptive ? "🔥 Адаптивный" :
        Workers == 1 ? "Последовательный (1)" : "Параллельный (" + Workers + ")";
    public string ChartLabel => IsBaseline ? "1" : IsAdaptive64 ? "🔥🔥64" : IsAdaptive ? "🔥 Auto" : Workers.ToString(CultureInfo.InvariantCulture);
    public long Objects => Files + Folders;
    public double ObjectsPerSecond => Seconds > 0 ? Objects / Seconds : 0;
    public string SecondsLabel => Seconds.ToString(IsBaseline ? "N6" : "N3", Ru) + " с";
    public string ObjectsLabel => ObjectsPerSecond.ToString("N0", Ru);
    public string SpeedupLabel => (PerformanceHistory.BaselineMilliseconds / (Seconds * 1000d)).ToString("N2", Ru) + "×";
    public string RunsLabel => Runs.ToString(Ru);
    public string CpuLabel => FormatOptional(CpuPercent, "N2", " %");
    public string QueueLabel => FormatOptional(QueueAverageMs, "N2", "");
    public string DirectoryLabel => FormatOptional(DirectoryWorkerSeconds, "N3", "");
    public string IdleLabel => FormatOptional(WorkerIdleSeconds, "N3", "");
    public string FilesLabel => Files.ToString("N0", Ru);
    public string FoldersLabel => Folders.ToString("N0", Ru);
    public string BytesLabel => Bytes.HasValue ? Bytes.Value.ToString("N0", Ru) : "—";
    public string SourceLabel => Source switch
    {
        "user-screenshot" => "Скриншот теста",
        "user-benchmarks.csv" => "Присланный тест",
        "local-csv" => "CSV приложения",
        "local-log" => "Журнал приложения",
        _ => Source
    };
    private static string FormatOptional(double? v, string format, string suffix) =>
        v.HasValue && double.IsFinite(v.Value) ? v.Value.ToString(format, Ru) + suffix : "—";
}

public static class PerformanceHistory
{
    // Verified from the first user-supplied benchmarks.csv row, not a median or a guess.
    public const double BaselineMilliseconds = 320990.849;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IReadOnlyList<HistoricalRun> BestObserved()
    {
        var all = ReadEmbeddedEvidence().ToList();
        // Only portable application-local logs are read; no AppData dependency.
        // Log reads happen on window open/refresh, never during the hot scan loop.
        string csv = Path.Combine(BenchmarkStore.Folder, "benchmarks_v1_2.csv");
        if (File.Exists(csv))
        {
            try { all.AddRange(ParseBenchmarkCsv(File.ReadLines(csv), "local-csv")); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        string events = Path.Combine(BenchmarkStore.Folder, "events.log");
        if (File.Exists(events))
        {
            try
            {
                foreach (string line in File.ReadLines(events))
                {
                    var x = TryParseEvent(line);
                    if (x is not null) all.Add(x);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Repeated exports of the same measured run must NOT inflate the run count.
        // One-second precision covers the user screenshot; exact CSV evidence has priority.
        all = all.Where(x => x.Milliseconds > 0 && double.IsFinite(x.Milliseconds) &&
                x.Workers is >= 1 and <= 64 && x.Files > 0 && x.Folders > 0)
            .GroupBy(x => (x.Key, DateSecond: x.Finished.ToUnixTimeSeconds()))
            .Select(g => g.OrderByDescending(x => EvidenceScore(x)).First())
            .ToList();

        var rows = new List<HistoricalRun>();
        // Portable v1.4.9: show exactly one sequential result, the original baseline.
        // Best later sequential runs remain historical evidence, not separate UI rows.
        var baseline = all.Where(x => x.Group == "seq")
            .OrderBy(x => Math.Abs(x.Milliseconds - BaselineMilliseconds)).FirstOrDefault();
        if (baseline is not null)
            rows.Add(ToRow(baseline, 1, isBaseline: true));

        foreach (var g in all.GroupBy(x => x.Key))
        {
            // No second sequential point (141.587s) or table row; the chart uses baseline.
            if (g.Key == "seq") continue;
            var winner = g.OrderBy(x => x.Milliseconds).First();
            rows.Add(ToRow(winner, g.Count()));
        }
        return rows.OrderBy(x => x.IsBaseline ? -1 : x.IsAdaptive64 ? 200 : x.IsAdaptive ? 100 : x.Workers)
            .ToArray();
    }

    private static int EvidenceScore(MeasuredPass p) =>
        (p.Bytes.HasValue ? 1 : 0) + (p.Cpu.HasValue ? 2 : 0) +
        (p.Queue.HasValue ? 2 : 0) + (p.Directory.HasValue ? 2 : 0) +
        (p.Source == "local-csv" ? 1 : 0);

    private static HistoricalRun ToRow(MeasuredPass p, int count, bool isBaseline = false) =>
        new(p.Workers, p.Milliseconds / 1000d, p.Files, p.Folders, p.Bytes,
            p.Finished.ToString("yyyy-MM-dd HH:mm:ss", Invariant), p.Source,
            p.Group is "adaptive32" or "adaptive64", p.Group == "adaptive64", isBaseline,
            count, p.Cpu, p.Queue, p.Directory, p.Idle);

    private static IEnumerable<MeasuredPass> ReadEmbeddedEvidence()
    {
        const string resourceEnd = ".historical_runs_v1_4_9.csv";
        Assembly asm = typeof(PerformanceHistory).Assembly;
        string? name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceEnd, StringComparison.OrdinalIgnoreCase));
        if (name is null) yield break;
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) yield break;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        reader.ReadLine(); // The bundled evidence includes a header.
        while (reader.ReadLine() is { } line)
        {
            var fields = SplitSemicolonCsv(line);
            if (fields.Count < 12) continue;
            var pass = ParsePass(fields);
            if (pass is not null) yield return pass;
        }
    }

    private static MeasuredPass? ParsePass(IReadOnlyList<string> f)
    {
        if (f.Count < 12 || !int.TryParse(f[1], out int workers) ||
            !double.TryParse(f[2], NumberStyles.Float, Invariant, out double ms) ||
            !long.TryParse(f[3], out long files) || !long.TryParse(f[4], out long folders) ||
            !DateTimeOffset.TryParse(f[6], Invariant, DateTimeStyles.None, out var finished)) return null;
        return new MeasuredPass(f[0], workers, ms, files, folders, Long(f[5]), finished,
            f[11], Double(f[7]), Double(f[8]), Double(f[9]), Double(f[10]));
    }

    private static IEnumerable<MeasuredPass> ParseBenchmarkCsv(IEnumerable<string> lines, string source)
    {
        using var iter = lines.GetEnumerator();
        if (!iter.MoveNext()) yield break;
        var header = SplitSemicolonCsv(iter.Current).Select((key, i) => (key, i))
            .ToDictionary(x => x.key, x => x.i, StringComparer.OrdinalIgnoreCase);
        if (!header.ContainsKey("duration_ms") || !header.ContainsKey("status")) yield break;
        while (iter.MoveNext())
        {
            var f = SplitSemicolonCsv(iter.Current);
            string Get(string key) => header.TryGetValue(key, out int idx) && idx < f.Count ? f[idx] : "";
            if (Get("status") != "Завершено" || Get("errors") != "0" ||
                !int.TryParse(Get("workers"), out var workers) ||
                !double.TryParse(Get("duration_ms"), NumberStyles.Float, Invariant, out var ms) ||
                !long.TryParse(Get("files"), out var files) ||
                !long.TryParse(Get("folders"), out var folders) ||
                !DateTimeOffset.TryParse(Get("finish_local"), Invariant, DateTimeStyles.None, out var finished)) continue;
            string group = ParseGroup(Get("mode"), workers);
            if (group == "") continue;
            yield return new MeasuredPass(group, workers, ms, files, folders,
                Long(Get("bytes")), finished, source, Double(Get("cpu_percent_all_logical_cores")),
                Double(Get("queue_wait_average_ms")), Double(Get("directory_worker_seconds")),
                Double(Get("worker_idle_total_seconds")));
        }
    }

    private static string ParseGroup(string mode, int workers)
    {
        if (mode.Contains("64 Адаптивный", StringComparison.OrdinalIgnoreCase)) return "adaptive64";
        if (mode.Contains("Адаптивный", StringComparison.OrdinalIgnoreCase)) return "adaptive32";
        if (mode == "Последовательный" && workers == 1) return "seq";
        if (mode == "Параллельный") return "fixed";
        return "";
    }

    private static MeasuredPass? TryParseEvent(string line)
    {
        if (!line.Contains("status=Завершено", StringComparison.Ordinal) ||
            !line.Contains("errors=0", StringComparison.Ordinal)) return null;
        var parts = line.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length < 9 || !DateTimeOffset.TryParse(parts[0], Invariant, DateTimeStyles.None, out var stamp)) return null;
        int workers = 0; double seconds = 0; long files = 0, folders = 0;
        long? bytes = null;
        foreach (string part in parts)
        {
            int k = part.IndexOf('='); if (k < 0) continue;
            string key = part[..k].Trim(), value = part[(k + 1)..].Trim();
            switch (key)
            {
                case "workers": int.TryParse(value, out workers); break;
                case "seconds": double.TryParse(value, NumberStyles.Float, Invariant, out seconds); break;
                case "files": long.TryParse(value, out files); break;
                case "folders": long.TryParse(value, out folders); break;
                case "bytes": bytes = Long(value); break;
            }
        }
        string group = ParseGroup(parts[2], workers);
        if (group == "" || seconds <= 0 || workers < 1 || files <= 0 || folders <= 0) return null;
        return new MeasuredPass(group, workers, seconds * 1000d, files, folders,
            bytes, stamp, "local-log", null, null, null, null);
    }

    private static double? Double(string s) => double.TryParse(s, NumberStyles.Float, Invariant, out double v) ? v : null;
    private static long? Long(string s) => long.TryParse(s, NumberStyles.Integer, Invariant, out long v) ? v : null;

    private static List<string> SplitSemicolonCsv(string line)
    {
        var a = new List<string>(); var sb = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ';' && !quoted) { a.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        a.Add(sb.ToString()); return a;
    }
}
