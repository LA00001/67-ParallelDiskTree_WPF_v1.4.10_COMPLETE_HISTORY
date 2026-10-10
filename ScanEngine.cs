using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Threading.Channels;

namespace ParallelDiskTree;

public sealed class ScanNode
{
    public string Name { get; }
    public string FullPath { get; }
    public ScanNode? Parent { get; }
    public ConcurrentDictionary<string, ScanNode> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentQueue<ScannedFile> Files { get; } = new();
    private long _bytes, _directBytes, _fileCount;
    private int _scanned;

    public long Bytes => Interlocked.Read(ref _bytes);
    public long DirectBytes => Interlocked.Read(ref _directBytes);
    public long FileCount => Interlocked.Read(ref _fileCount);
    public bool Scanned => Volatile.Read(ref _scanned) != 0;

    public ScanNode(string path, ScanNode? parent = null)
    {
        FullPath = path;
        Parent = parent;
        Name = parent is null ? path : Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
    }

    internal void AddFile(long length)
    {
        Interlocked.Add(ref _directBytes, length);
        for (ScanNode? node = this; node is not null; node = node.Parent)
        {
            Interlocked.Add(ref node._bytes, length);
            Interlocked.Increment(ref node._fileCount);
        }
    }

    internal void MarkScanned() => Volatile.Write(ref _scanned, 1);
}

public sealed record ScannedFile(string Name, string FullPath, long Bytes);
public sealed record ScanProgress(long Files, long Folders, long Bytes, long Errors,
    int Pending, int Active, bool Completed, bool Cancelled);

// These are CUMULATIVE WORKER TIMES, not wall-clock elapsed times.
// Directory processing includes enumeration AND adding discovered metadata to the tree.
public sealed record ScanTiming(double DirectoryWorkerSeconds, double QueueWaitSeconds,
    double IdleWorkerSeconds, long QueueSamples)
{
    public double MeanQueueWaitMilliseconds => QueueSamples == 0 ? 0 : QueueWaitSeconds * 1000 / QueueSamples;
}

public sealed class ScanEngine
{
    private readonly int _workers;
    private readonly bool _sequential;
    private readonly bool _adaptive;
    private int _targetWorkers;
    private readonly ConcurrentQueue<AdaptiveSample> _adaptiveHistory = new();
    private AdaptiveSample? _latestAdaptive;
    private readonly ConcurrentQueue<string> _errors = new();
    private long _files, _folders, _errorsCount;
    private long _directoryTicks, _queueTicks, _idleTicks, _queueSamples;
    private int _pending, _active;
    private volatile bool _completed, _cancelled;
    private readonly record struct Work(ScanNode Node, long EnqueuedTicks);

    public ScanNode Root { get; }
    public IReadOnlyList<string> Errors => _errors.ToArray();
    public int Workers => _sequential ? 1 : _workers;
    public bool IsSequential => _sequential;
    public bool IsAdaptive => _adaptive;
    public int TargetWorkers => _adaptive ? Volatile.Read(ref _targetWorkers) : Workers;
    public AdaptiveSample? LatestAdaptive => Volatile.Read(ref _latestAdaptive);
    public IReadOnlyList<AdaptiveSample> AdaptiveHistory => _adaptiveHistory.ToArray();

    public ScanEngine(string root, int workers = 4, bool sequential = false, bool adaptive = false)
    {
        Root = new ScanNode(Path.GetFullPath(root));
        if (workers < 1 || workers > 64)
            throw new ArgumentOutOfRangeException(nameof(workers), "Выберите от 1 до 64 исполнителей.");
        _workers = workers;
        _sequential = sequential;
        _adaptive = adaptive;
        if (adaptive && (sequential || workers < 4 || workers > 64))
            throw new ArgumentException("Адаптивному режиму требуется предел от 4 до 64 исполнителей.");
        _targetWorkers = adaptive ? 4 : workers;
    }

    public ScanProgress Progress => new(Interlocked.Read(ref _files),
        Interlocked.Read(ref _folders), Root.Bytes, Interlocked.Read(ref _errorsCount),
        Volatile.Read(ref _pending), Volatile.Read(ref _active), _completed, _cancelled);

    public ScanTiming Timing => new(
        Interlocked.Read(ref _directoryTicks) / (double)Stopwatch.Frequency,
        Interlocked.Read(ref _queueTicks) / (double)Stopwatch.Frequency,
        Interlocked.Read(ref _idleTicks) / (double)Stopwatch.Frequency,
        Interlocked.Read(ref _queueSamples));

    public async Task ScanAsync(CancellationToken token)
    {
        if (_completed) throw new InvalidOperationException("Для повторного сканирования создайте новый ScanEngine.");
        try
        {
            if (_sequential)
                await Task.Run(() => ScanSequential(token), CancellationToken.None).ConfigureAwait(false);
            else
                await ScanParallelAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _cancelled = token.IsCancellationRequested;
            _completed = true;
        }
    }

    private void ScanSequential(CancellationToken token)
    {
        var stack = new Stack<ScanNode>();
        stack.Push(Root);
        Volatile.Write(ref _pending, 1);
        try
        {
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var node = stack.Pop();
                Volatile.Write(ref _active, 1);
                try { ProcessDirectory(node, child => stack.Push(child), token); }
                finally
                {
                    Volatile.Write(ref _active, 0);
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task ScanParallelAsync(CancellationToken token)
    {
        var channel = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions
        {
            SingleReader = false, SingleWriter = false, AllowSynchronousContinuations = false
        });
        Volatile.Write(ref _pending, 1);
        channel.Writer.TryWrite(new Work(Root, Stopwatch.GetTimestamp()));

        // Spawn up to the selected ceiling (32 or 64) once. Workers above the current
        // target remain parked; no directories are ever rescanned to adapt N.
        using var controlCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var controller = _adaptive ? Task.Run(() => AdaptiveControlAsync(controlCts.Token), CancellationToken.None) : Task.CompletedTask;
        var tasks = Enumerable.Range(0, _workers).Select(workerId => Task.Run(async () =>
        {
            try
            {
                // Time spent WAITING for work is separate from time processing directories.
                while (true)
                {
                    if (_adaptive && workerId >= Volatile.Read(ref _targetWorkers))
                    {
                        if (channel.Reader.Completion.IsCompleted) break;
                        await Task.Delay(200, token).ConfigureAwait(false);
                        continue;
                    }
                    var idleStart = Stopwatch.GetTimestamp();
                    bool available = await channel.Reader.WaitToReadAsync(token).ConfigureAwait(false);
                    Interlocked.Add(ref _idleTicks, Stopwatch.GetTimestamp() - idleStart);
                    if (!available) break;
                    while ((!_adaptive || workerId < Volatile.Read(ref _targetWorkers)) &&
                           channel.Reader.TryRead(out Work item))
                    {
                        long dequeuedAt = Stopwatch.GetTimestamp();
                        Interlocked.Add(ref _queueTicks, dequeuedAt - item.EnqueuedTicks);
                        Interlocked.Increment(ref _queueSamples);
                        Interlocked.Increment(ref _active);
                        try
                        {
                            ProcessDirectory(item.Node, child =>
                            {
                                if (!channel.Writer.TryWrite(new Work(child, Stopwatch.GetTimestamp())))
                                    throw new IOException("Не удалось поставить папку в очередь: " + child.FullPath);
                            }, token);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _active);
                            if (Interlocked.Decrement(ref _pending) == 0)
                                channel.Writer.TryComplete();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }, CancellationToken.None)).ToArray();

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        finally
        {
            channel.Writer.TryComplete();
            controlCts.Cancel();
            try { await controller.ConfigureAwait(false); }
            catch (OperationCanceledException) when (controlCts.IsCancellationRequested) { }
        }
    }

    // Independent low-frequency control loop. PDH is sampled once per ~2 seconds;
    // throughput and scaling decisions are considered only once per ~8 seconds.
    private async Task AdaptiveControlAsync(CancellationToken token)
    {
        using var counters = new WindowsDiskCounters(Root.FullPath);
        var watch = Stopwatch.StartNew();
        long prevTotal = 0;
        double prevTime = 0;
        double priorWindowRate = 0;
        long decisionObjectCount = 0;
        double decisionElapsed = 0;
        int lastChangedLevel = 4;
        double nextGrowthAfter = 0;
        int step = 0;
        try
        {
            while (true)
            {
                await Task.Delay(2000, token).ConfigureAwait(false);
                double elapsed = watch.Elapsed.TotalSeconds;
                long total = Interlocked.Read(ref _files) + Interlocked.Read(ref _folders);
                int current = Volatile.Read(ref _targetWorkers);
                int backlog = Math.Max(0, Volatile.Read(ref _pending) - Volatile.Read(ref _active));
                int active = Volatile.Read(ref _active);
                DiskSnapshot disk = counters.Read();
                double rate = (total - prevTotal) / Math.Max(.001, elapsed - prevTime);
                prevTotal = total;
                prevTime = elapsed;
                string decision = "Наблюдение";

                // 8s windows: compare only coarse-grained data; never claim
                // proof that a throughput variation was CAUSED by a worker change.
                if (++step % 4 == 0)
                {
                    // Use aggregate throughput over the entire ~8-second decision
                    // window, not a noisy two-second sample.
                    double windowRate = (total - decisionObjectCount) /
                        Math.Max(.001, elapsed - decisionElapsed);
                    decisionObjectCount = total;
                    decisionElapsed = elapsed;
                    bool backlogEnough = backlog >= current * 3 && active >= Math.Max(2, current / 2);
                    bool ioStruggling = disk.LatencyMilliseconds is > 35 && disk.BusyPercent is > 95;
                    bool rateDeclined = priorWindowRate > 100 && windowRate < priorWindowRate * .70 &&
                        lastChangedLevel < current;
                    if (current > 4 && backlogEnough && (ioStruggling || rateDeclined))
                    {
                        int reduced = Math.Max(4, current / 2);
                        Volatile.Write(ref _targetWorkers, reduced);
                        decision = $"Снижение {current}→{reduced}: " + (ioStruggling ? "высокая задержка накопителя" : "снижение скорости, эвристика");
                        nextGrowthAfter = elapsed + 24;
                        lastChangedLevel = reduced;
                    }
                    else if (current < _workers && backlogEnough && elapsed >= nextGrowthAfter &&
                             !ioStruggling && !rateDeclined)
                    {
                        int raised = Math.Min(_workers, current * 2);
                        Volatile.Write(ref _targetWorkers, raised);
                        decision = $"Увеличение {current}→{raised}: очередь заполнена";
                        lastChangedLevel = current;
                    }
                    else if (!backlogEnough)
                        decision = "Без изменения: недостаточно параллельной работы";
                    else if (ioStruggling)
                        decision = "Без увеличения: задержка накопителя высока";
                    else if (elapsed < nextGrowthAfter)
                        decision = "Пауза после снижения исполнителей";
                    else
                        decision = "Без изменения: проверка эффективности";

                    priorWindowRate = windowRate;
                }

                var sample = new AdaptiveSample(DateTimeOffset.Now, elapsed,
                    Volatile.Read(ref _targetWorkers), active, backlog, rate,
                    disk.BusyPercent, disk.LatencyMilliseconds, disk.BytesPerSecond, decision);
                Volatile.Write(ref _latestAdaptive, sample);
                if (_adaptiveHistory.Count < 1800) _adaptiveHistory.Enqueue(sample);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void ProcessDirectory(ScanNode node, Action<ScanNode> schedule, CancellationToken token)
    {
        long dirStart = Stopwatch.GetTimestamp();
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false
            };
            var entries = new FileSystemEnumerable<(string Path, string Name, bool IsDir, bool IsLink, long Length)>(
                node.FullPath,
                (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.FileName.ToString(),
                    entry.IsDirectory, (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                    entry.IsDirectory ? 0 : entry.Length),
                options);

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.IsDir)
                {
                    // Avoid junction and symlink loops; do not traverse directory reparse points.
                    if (entry.IsLink) continue;
                    var child = new ScanNode(entry.Path, node);
                    if (node.Folders.TryAdd(entry.Name, child))
                    {
                        Interlocked.Increment(ref _folders);
                        Interlocked.Increment(ref _pending);
                        try { schedule(child); }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException)
                        {
                            Interlocked.Decrement(ref _pending);
                            RecordError(child.FullPath + " — " + ex.Message);
                        }
                    }
                }
                else
                {
                    var length = Math.Max(0, entry.Length);
                    node.Files.Enqueue(new ScannedFile(entry.Name, entry.Path, length));
                    node.AddFile(length);
                    Interlocked.Increment(ref _files);
                }
            }
            node.MarkScanned();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or PathTooLongException)
        {
            RecordError(node.FullPath + " — " + ex.Message);
        }
        finally
        {
            // One stopwatch sample per DIRECTORY (never per file).
            Interlocked.Add(ref _directoryTicks, Stopwatch.GetTimestamp() - dirStart);
        }
    }

    private void RecordError(string message)
    {
        Interlocked.Increment(ref _errorsCount);
        if (_errors.Count < 100) _errors.Enqueue(message);
    }
}
