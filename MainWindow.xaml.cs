using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ParallelDiskTree;

public partial class MainWindow : Window
{
    private ScanEngine? _engine;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _scanWatch = new();
    private ScanNode? _selected;
    private const int PageSize = 500;
    private int _page;
    // Separate folder-location history from the 500-row pagination mechanism.
    private readonly List<ScanNode> _folderHistory = new();
    private int _folderHistoryIndex = -1;
    private List<RowItem> _rows = new();
    private long _lastSelectedFiles = -1, _lastSelectedFolders = -1;
    private TreeViewItem? _rootItem;
    private bool _isRunning;
    private AdaptiveSample? _shownAdaptive;
    private GridLength _expandedTreeWidth = new(340);

    // Seven independent section controls. No scanning data is removed when a panel is folded.
    private void ToggleSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string targetName ||
            FindName(targetName) is not FrameworkElement target)
            return;

        bool collapse = target.Visibility == Visibility.Visible;
        if (targetName == nameof(FolderTree))
        {
            if (collapse)
            {
                _expandedTreeWidth = TreeColumn.Width;
                TreeColumn.Width = new GridLength(64, GridUnitType.Pixel);
                TreeDivider.IsEnabled = false;
                FolderTreeTitle.Visibility = Visibility.Collapsed;
            }
            else
            {
                TreeColumn.Width = _expandedTreeWidth.Value >= 100
                    ? _expandedTreeWidth : new GridLength(340);
                TreeDivider.IsEnabled = true;
                FolderTreeTitle.Visibility = Visibility.Visible;
            }
        }
        target.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        button.Content = collapse ? "▶" : "▼";
        button.ToolTip = collapse ? "Развернуть раздел" : "Свернуть раздел";
    }

    private sealed record RowItem(string Kind, string Name, string FullPath, long Bytes, ScanNode? Folder)
    {
        public string Pretty => FormatSize(Bytes);
    }

    public MainWindow()
    {
        InitializeComponent();

        // A fixed 1560 x 900 window could be partially off-screen on displays
        // with a smaller usable area or with high Windows DPI scaling.
        FitInitialWindowToWorkingArea();
        // The page scrolls vertically only. Always show its beginning on launch.
        Loaded += (_, _) => PageScroll.ScrollToTop();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _timer.Tick += (_, _) => RefreshProgress();
    }

    private void FitInitialWindowToWorkingArea()
    {
        // SystemParameters.WorkArea is expressed in WPF logical units (DIPs).
        // Unlike pixel-based display bounds, this respects Windows scaling.
        Rect area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0) return;

        const double inset = 12;
        double usableWidth = Math.Max(1, area.Width - 2 * inset);
        double usableHeight = Math.Max(1, area.Height - 2 * inset);

        // Do not let minimum-window-size constraints push the window off-screen.
        MinWidth = Math.Min(MinWidth, usableWidth);
        MinHeight = Math.Min(MinHeight, usableHeight);
        Width = Math.Min(1560, usableWidth);
        Height = Math.Min(900, usableHeight);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes.ToString("N0", CultureInfo.CurrentCulture) + " B";
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };
        double value = bytes;
        int index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return value.ToString("N2", CultureInfo.CurrentCulture) + " " + units[index];
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim().Trim('"');
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите корень сканирования",
            InitialDirectory = Directory.Exists(path)
                ? path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FolderName;
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunCommandAsync(compare: false);
    private async void Adaptive_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        ModeBox.SelectedIndex = 2;
        await RunCommandAsync(compare: false);
    }
    private async void Adaptive64_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        ModeBox.SelectedIndex = 3;
        await RunCommandAsync(compare: false);
    }
    private async void Compare_Click(object sender, RoutedEventArgs e) => await RunCommandAsync(compare: true);
    private async void AutoBenchmark_Click(object sender, RoutedEventArgs e) => await RunCommandAsync(compare: false, automatic: true);
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is null) return;
        StopButton.IsEnabled = false;
        StatusText.Text = "Остановка запрошена: ожидаем завершения активных операций перечисления…";
        _cts.Cancel();
    }

    private async Task RunCommandAsync(bool compare, bool automatic = false)
    {
        if (_isRunning) return;
        var path = PathBox.Text.Trim().Trim('"');
        if (!Directory.Exists(path))
        {
            MessageBox.Show(this, "Папка не существует или недоступна.", "Проверьте путь");
            return;
        }
        bool fixedWorkersRequired = compare || automatic || ModeBox.SelectedIndex < 2;
        if ((!int.TryParse(WorkersBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
            out int parallelWorkers) || parallelWorkers is < 1 or > 64) && fixedWorkersRequired)
        {
            MessageBox.Show(this, "Введите целое число исполнителей от 1 до 64.", "Число исполнителей");
            WorkersBox.Focus();
            WorkersBox.SelectAll();
            return;
        }

        // Compute the adaptive ceiling once in this method's scope. A nested
        // declaration with the same name caused CS0136 in the Windows build.
        int adaptiveCeiling = ModeBox.SelectedIndex == 3 ? 64 : 32;
        var specs = new List<ScanSpec>();
        if (automatic)
        {
            string[] parts = AutoWorkersBox.Text.Split(new[] { ',', ';', ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || parts.Length > 12 ||
                parts.Any(x => !int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n is < 1 or > 64) ||
                !int.TryParse(RepeatsBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int repeats) || repeats is < 1 or > 5)
            {
                MessageBox.Show(this, "Введите до 12 уровней исполнителей (1..64), например 1,2,4,8,16,32, и 1..5 повторов.", "Параметры автобенчмарка");
                return;
            }
            int[] levels = parts.Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            if (levels.Distinct().Count() != levels.Length)
            {
                MessageBox.Show(this, "Уровни исполнителей не должны повторяться.", "Параметры автобенчмарка");
                return;
            }
            specs.AddRange(BenchmarkAnalyzer.BuildSchedule(levels, repeats));
        }
        else if (compare)
        {
            var sequential = new ScanSpec(true, 1, 1, 1);
            var parallel = new ScanSpec(false, parallelWorkers, 1, 2);
            if (ParallelFirstBox.IsChecked == true)
            {
                specs.Add(parallel with { Order = 1 });
                specs.Add(sequential with { Order = 2 });
            }
            else
            {
                specs.Add(sequential);
                specs.Add(parallel);
            }
        }
        else
        {
            bool sequential = ModeBox.SelectedIndex == 0;
            bool adaptive = ModeBox.SelectedIndex is 2 or 3;
            specs.Add(new ScanSpec(sequential, adaptive ? adaptiveCeiling : sequential ? 1 : parallelWorkers, 1, 1));
        }

        _isRunning = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var sessionId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..6];
        var results = new List<BenchmarkResult>();
        SetBusy(true);
        BenchmarkGrid.ItemsSource = null;
        MedianGrid.ItemsSource = null;
        bool adaptiveMode = !automatic && !compare && (ModeBox.SelectedIndex is 2 or 3);
        string adaptiveLabel = adaptiveCeiling == 64 ? "🔥🔥64 Адаптивный" : "🔥 Адаптивный";
        AdaptiveStatsPanel.Visibility = adaptiveMode ? Visibility.Visible : Visibility.Collapsed;
        AdaptiveHistoryPanel.Visibility = adaptiveMode ? Visibility.Visible : Visibility.Collapsed;
        _shownAdaptive = null;
        AdaptiveHistoryGrid.ItemsSource = null;
        AdaptiveLiveText.Text = $"{adaptiveLabel}: старт 4, предел {adaptiveCeiling}; ожидание первых показателей…";
        DiskLiveText.Text = "Windows PDH: Disk Time н/д · задержка н/д · скорость н/д";
        ComparisonText.Text = adaptiveMode ? $"{adaptiveLabel}: один проход, старт 4; максимум {adaptiveCeiling}, при ухудшении снижение. PDH мониторит весь логический том." : automatic
            ? $"Автобенчмарк: {specs.Count} проходов, {specs.Max(x => x.Repeat)} повтор(а). Порядок чередуется, 1 = последовательный. Остановить можно в любой момент."
            : compare ? "A/B тест: готовлю первый проход…" : "Выполняется одиночный замер…";

        try
        {
            for (int index = 0; index < specs.Count; index++)
            {
                if (token.IsCancellationRequested) break;
                var spec = specs[index];
                ClearScanView();
                if (index > 0) GC.Collect(); // BETWEEN runs, not within measured time

                _engine = new ScanEngine(path, spec.Workers, spec.Sequential, adaptive: adaptiveMode);
                PrepareScanView(_engine);
                string mode = adaptiveMode ? adaptiveLabel : spec.Mode;
                StatusText.Text = $"Проход {index + 1}/{specs.Count}, повтор {spec.Repeat}: {mode}, " +
                    (adaptiveMode ? $"старт 4, предел {adaptiveCeiling}" : $"исполнителей {_engine.Workers}") +
                    ". Файлы не читаются целиком — только метаданные.";

                // CPU time is process-wide (scanner + responsive UI). It is NOT
                // CPU utilisation of a specific worker, nor a disk I/O metric.
                using var process = Process.GetCurrentProcess();
                var cpuStart = process.TotalProcessorTime;
                var startedAt = DateTimeOffset.Now;
                _scanWatch.Restart();
                _timer.Start();
                Exception? failure = null;
                try { await _engine.ScanAsync(token); }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    _scanWatch.Stop();
                    _timer.Stop();
                }
                var elapsed = _scanWatch.Elapsed;
                var endedAt = DateTimeOffset.Now;
                double cpuSeconds = Math.Max(0, (process.TotalProcessorTime - cpuStart).TotalSeconds);
                double cpuPercent = elapsed.TotalSeconds > 0
                    ? Math.Clamp(cpuSeconds / (elapsed.TotalSeconds * Environment.ProcessorCount) * 100, 0, 100)
                    : 0;
                RefreshProgress(force: true);
                var progress = _engine.Progress;
                var timing = _engine.Timing;
                string status = failure is not null ? "Ошибка" : progress.Cancelled ? "Отменено" : "Завершено";
                var result = new BenchmarkResult(sessionId, _engine.Root.FullPath, mode, _engine.Workers,
                    startedAt, endedAt, elapsed, progress.Files, progress.Folders, progress.Bytes,
                    progress.Errors, status, spec.Repeat, spec.Order, cpuSeconds, cpuPercent,
                    timing.DirectoryWorkerSeconds, timing.QueueWaitSeconds,
                    timing.IdleWorkerSeconds, timing.QueueSamples);
                results.Add(result);
                BenchmarkGrid.ItemsSource = results.ToArray();
                var partialSummary = BenchmarkAnalyzer.Summarize(results);
                MedianGrid.ItemsSource = partialSummary;
                ElapsedText.Text = result.ElapsedText;
                if (automatic)
                    ComparisonText.Text = BenchmarkAnalyzer.Explain(results, partialSummary, specs.Count);
                else if (adaptiveMode)
                    ComparisonText.Text = $"{adaptiveLabel}: {elapsed.TotalSeconds:N2} с; файлов {progress.Files:N0}, папок {progress.Folders:N0}, ошибок {progress.Errors:N0}. " +
                        $"Последняя цель: {_engine.TargetWorkers} исполнителей. Показатели PDH относятся ко всему выбранному тому.";
                if (failure is not null)
                {
                    StatusText.Text = "Ошибка: " + failure.Message;
                    break;
                }
                if (token.IsCancellationRequested) break;
            }

            var summaries = BenchmarkAnalyzer.Summarize(results);
            string? comparison = automatic
                ? BenchmarkAnalyzer.Explain(results, summaries, specs.Count)
                : compare ? MakeComparison(results) : null;
            ComparisonText.Text = (adaptiveMode && results.Count > 0) ? ComparisonText.Text : comparison ?? (results.Count > 0
                ? $"{results[0].Mode}: {results[0].ElapsedText}; исполнителей: {results[0].Workers}; ошибок: {results[0].Errors:N0}."
                : "Замеры не выполнены.");
            MedianGrid.ItemsSource = summaries;
            if (results.Count > 0)
            {
                // Logs are written only AFTER all stopwatch timings, never on scan hot path.
                try
                {
                    await BenchmarkStore.SaveAsync(results, comparison, summaries);
                    if (adaptiveMode && _engine is not null)
                        await AdaptiveMetricsStore.SaveAsync(sessionId, _engine.Root.FullPath, _engine.AdaptiveHistory);
                    StatusText.Text = $"Сохранено {results.Count} замеров из {specs.Count}. Логи: {BenchmarkStore.Folder}. " +
                        "Дерево последнего прохода доступно для просмотра и экспорта.";
                }
                catch (Exception ex)
                {
                    StatusText.Text = "Сканирование завершилось; ошибка сохранения логов: " + ex.Message;
                }
            }
            else StatusText.Text = "Запуск отменён до начала сканирования.";
        }
        catch (Exception ex)
        {
            ComparisonText.Text = "Ошибка подготовки бенчмарка: " + ex.Message;
            StatusText.Text = ex.ToString();
        }
        finally
        {
            _timer.Stop();
            _scanWatch.Stop();
            BusyBar.IsIndeterminate = false;
            BusyBar.Value = 100;
            _isRunning = false;
            SetBusy(false);
        }
    }

    private static string MakeComparison(IReadOnlyList<BenchmarkResult> results)
    {
        if (results.Count != 2)
            return "A/B не завершён: для сравнения нужны два полных прохода.";
        var single = results.FirstOrDefault(r => r.Mode == "Последовательный");
        var parallel = results.FirstOrDefault(r => r.Mode == "Параллельный");
        if (single is null || parallel is null)
            return "A/B не выполнен: не хватает одного из режимов.";
        if (single.Status != "Завершено" || parallel.Status != "Завершено")
            return "A/B неполный: один из проходов завершился с ошибкой или был отменён.";

        bool countsMatch = single.Bytes == parallel.Bytes && single.Files == parallel.Files &&
            single.Folders == parallel.Folders;
        string matching = countsMatch ? "Счётчики файлов, папок и байтов совпали. " :
            "ВНИМАНИЕ: результаты по числу объектов или байтам различаются. ";
        if (single.Errors > 0 || parallel.Errors > 0)
            matching += "Есть ошибки обхода: сравнение неполное. ";

        if (single.Elapsed.TotalSeconds <= 0 || parallel.Elapsed.TotalSeconds <= 0)
            return matching + "Недостаточно данных для сравнения времени.";
        double factor = single.Elapsed.TotalSeconds / parallel.Elapsed.TotalSeconds;
        double saved = 100 * (single.Elapsed.TotalSeconds - parallel.Elapsed.TotalSeconds) /
            single.Elapsed.TotalSeconds;
        string speed = factor >= 1
            ? $"Параллельный ({parallel.Workers}) быстрее в {factor:N2} раза; экономия {saved:N1}% времени."
            : $"Последовательный быстрее в {(1 / factor):N2} раза; параллельный дольше на {-saved:N1}% относительно обычного.";
        return matching + speed + " Важно: второй проход может выигрывать за счёт кэша Windows.";
    }

    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = !busy;
        CompareButton.IsEnabled = !busy;
        AutoButton.IsEnabled = !busy;
        AdaptiveButton.IsEnabled = !busy;
        Adaptive64Button.IsEnabled = !busy;
        AutoWorkersBox.IsEnabled = !busy;
        RepeatsBox.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        PathBox.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        WorkersBox.IsEnabled = !busy;
        ModeBox.IsEnabled = !busy;
        ParallelFirstBox.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy && _engine is not null;
        BusyBar.IsIndeterminate = busy;
    }

    private void ClearScanView()
    {
        FolderTree.Items.Clear();
        ItemsGrid.ItemsSource = null;
        PreviousButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        PreviousPageButton.IsEnabled = false;
        NextPageButton.IsEnabled = false;
        PageText.Text = "";
        _folderHistory.Clear();
        _folderHistoryIndex = -1;
        _rows.Clear();
        _selected = null;
        _rootItem = null;
        _engine = null;
        _page = 0;
        _lastSelectedFiles = -1;
        _lastSelectedFolders = -1;
        _shownAdaptive = null;
    }

    private void PrepareScanView(ScanEngine engine)
    {
        _selected = null;
        _rootItem = MakeTreeItem(engine.Root);
        FolderTree.Items.Add(_rootItem);
        NavigateToFolder(engine.Root);
        _rootItem.IsSelected = true;
        TotalSizeText.Text = "0 B";
        FilesText.Text = "0";
        FoldersText.Text = "0";
        QueueText.Text = "0 / 0 / 0";
        ElapsedText.Text = "0,000 с";
    }

    private TreeViewItem MakeTreeItem(ScanNode node)
    {
        var item = new TreeViewItem { Header = node.Name, Tag = node };
        item.Items.Add("…");
        item.Expanded += Folder_Expanded;
        return item;
    }

    private void Folder_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != sender) return;
        var item = (TreeViewItem)sender;
        if (item.Tag is ScanNode node) LoadFolderChildren(item, node);
    }

    private void LoadFolderChildren(TreeViewItem item, ScanNode node)
    {
        item.Items.Clear();
        foreach (var child in node.Folders.Values.OrderByDescending(x => x.Bytes)
                     .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(3000))
            item.Items.Add(MakeTreeItem(child));
        if (node.Folders.Count > 3000)
            item.Items.Add("Показаны первые 3000 папок; полный список справа постранично.");
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem item && item.Tag is ScanNode node)
            NavigateToFolder(node);
    }

    private void RefreshProgress(bool force = false)
    {
        if (_engine is null) return;
        var p = _engine.Progress;
        TotalSizeText.Text = FormatSize(p.Bytes);
        FilesText.Text = p.Files.ToString("N0");
        FoldersText.Text = p.Folders.ToString("N0");
        QueueText.Text = $"{p.Pending:N0} / {p.Active} / {p.Errors:N0}";
        if (_engine.IsAdaptive)
        {
            var point = _engine.LatestAdaptive;
            if (point is not null)
            {
                string label = _engine.Workers == 64 ? "🔥🔥64 Адаптивный" : "🔥 Адаптивный";
                AdaptiveLiveText.Text = $"{label}: цель {_engine.TargetWorkers} / активных {p.Active}, скорость {point.ObjectsText} объектов/с · {point.Decision}";
                DiskLiveText.Text = $"Windows PDH: Disk Time {point.BusyText} · задержка {point.LatencyText} мс · {point.ThroughputText} MiB/с (весь том, не только сканер)";
                if (!ReferenceEquals(_shownAdaptive, point))
                {
                    _shownAdaptive = point;
                    AdaptiveHistoryGrid.ItemsSource = _engine.AdaptiveHistory.TakeLast(40).Reverse().ToArray();
                }
            }
        }
        if (_scanWatch.IsRunning) ElapsedText.Text = _scanWatch.Elapsed.TotalSeconds.ToString("N3") + " с";
        if (force || _selected?.Files.Count != _lastSelectedFiles ||
            _selected?.Folders.Count != _lastSelectedFolders)
            RefreshSelection();
        else if (_selected is not null)
            CurrentBytes.Text = $"{_selected.Bytes:N0} байт ({FormatSize(_selected.Bytes)}) · файлов внутри: {_selected.FileCount:N0}";
    }

    private void RefreshSelection()
    {
        if (_selected is null) return;
        CurrentPath.Text = _selected.FullPath;
        CurrentBytes.Text = $"{_selected.Bytes:N0} байт ({FormatSize(_selected.Bytes)}) · файлов внутри: {_selected.FileCount:N0}";
        _lastSelectedFiles = _selected.Files.Count;
        _lastSelectedFolders = _selected.Folders.Count;
        _rows = _selected.Folders.Values.Select(node => new RowItem("📁", node.Name, node.FullPath, node.Bytes, node))
            .Concat(_selected.Files.Select(file => new RowItem("📄", file.Name, file.FullPath, file.Bytes, null)))
            .OrderByDescending(x => x.Bytes).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ShowPage();
    }

    private void ShowPage()
    {
        _page = Math.Clamp(_page, 0, Math.Max(0, (_rows.Count - 1) / PageSize));
        ItemsGrid.ItemsSource = _rows.Skip(_page * PageSize).Take(PageSize).ToArray();
        int pageCount = Math.Max(1, (_rows.Count + PageSize - 1) / PageSize);
        PageText.Text = $"Страница {_page + 1} / {pageCount} · всего {_rows.Count:N0} элементов";
        // Page navigation is independent of folder navigation history.
        PreviousPageButton.IsEnabled = _page > 0;
        NextPageButton.IsEnabled = _page + 1 < pageCount;
        UpdateFolderNavigationButtons();
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_page <= 0) return;
        _page--;
        ShowPage();
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if ((_page + 1) * PageSize >= _rows.Count) return;
        _page++;
        ShowPage();
    }

    private void NavigateToFolder(ScanNode node, bool addToHistory = true)
    {
        // Branching from an older history entry drops its forward branch,
        // matching the Windows Explorer Back/Forward behaviour.
        if (addToHistory && (_folderHistoryIndex < 0 ||
            !ReferenceEquals(_folderHistory[_folderHistoryIndex], node)))
        {
            int forwardStart = _folderHistoryIndex + 1;
            if (forwardStart < _folderHistory.Count)
                _folderHistory.RemoveRange(forwardStart, _folderHistory.Count - forwardStart);
            _folderHistory.Add(node);
            _folderHistoryIndex = _folderHistory.Count - 1;
        }

        if (!ReferenceEquals(_selected, node))
        {
            _selected = node;
            _page = 0;
            _lastSelectedFiles = -1;
            _lastSelectedFolders = -1;
            RefreshSelection();
        }
        UpdateFolderNavigationButtons();
        SyncTreeSelectionIfLoaded(node);
    }

    // Align the left-tree highlight with Back/Forward navigation if the item
    // is already present in the tree. Do not expand or enumerate new branches:
    // large folder trees must stay responsive during navigation.
    private void SyncTreeSelectionIfLoaded(ScanNode node)
    {
        if (_rootItem is null) return;
        if (FolderTree.SelectedItem is TreeViewItem selected &&
            ReferenceEquals(selected.Tag, node)) return;

        var path = new Stack<ScanNode>();
        for (ScanNode? current = node; current is not null &&
             !ReferenceEquals(current, _rootItem.Tag); current = current.Parent)
            path.Push(current);

        TreeViewItem? item = _rootItem;
        while (path.Count > 0 && item is not null)
        {
            var expected = path.Pop();
            item = item.Items.OfType<TreeViewItem>()
                .FirstOrDefault(child => ReferenceEquals(child.Tag, expected));
        }

        if (item is not null)
            item.IsSelected = true;
        else if (FolderTree.SelectedItem is TreeViewItem obsolete)
            obsolete.IsSelected = false;
    }

    private void UpdateFolderNavigationButtons()
    {
        PreviousButton.IsEnabled = _folderHistoryIndex > 0;
        NextButton.IsEnabled = _folderHistoryIndex >= 0 &&
            _folderHistoryIndex + 1 < _folderHistory.Count;
        PreviousButton.ToolTip = PreviousButton.IsEnabled
            ? "Назад к: " + _folderHistory[_folderHistoryIndex - 1].FullPath
            : "История папок: нет предыдущей папки";
        NextButton.ToolTip = NextButton.IsEnabled
            ? "Вперёд к: " + _folderHistory[_folderHistoryIndex + 1].FullPath
            : "История папок: нет следующей папки";
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_folderHistoryIndex <= 0) return;
        _folderHistoryIndex--;
        NavigateToFolder(_folderHistory[_folderHistoryIndex], addToHistory: false);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_folderHistoryIndex < 0 || _folderHistoryIndex + 1 >= _folderHistory.Count) return;
        _folderHistoryIndex++;
        NavigateToFolder(_folderHistory[_folderHistoryIndex], addToHistory: false);
    }

    private void ItemsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsGrid.SelectedItem is not RowItem item) return;
        if (item.Folder is not null)
        {
            NavigateToFolder(item.Folder);
        }
        else OpenPath(item.FullPath, selectFile: true);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) OpenPath(_selected.FullPath, selectFile: false);
    }

    private static void OpenPath(string path, bool selectFile)
    {
        try
        {
            if (selectFile)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось открыть"); }
    }

    // History window only reads logs when requested, never while scan hot path runs.
    private void Statistics_Click(object sender, RoutedEventArgs e)
    {
        new PerformanceWindow { Owner = this }.Show();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(BenchmarkStore.Folder);
            OpenPath(BenchmarkStore.Folder, selectFile: false);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось открыть папку логов"); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_engine is null || _isRunning)
        {
            MessageBox.Show("Сначала завершите сканирование.");
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV UTF-8 (*.csv)|*.csv",
            FileName = "disk-tree.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        var root = _engine.Root;
        ExportButton.IsEnabled = false;
        StatusText.Text = "Экспортирую дерево…";
        try
        {
            await Task.Run(() =>
            {
                using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
                writer.WriteLine("type;path;bytes;scan_complete");
                var stack = new Stack<ScanNode>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    var folder = stack.Pop();
                    writer.WriteLine($"folder;{Csv(folder.FullPath)};{folder.Bytes};{folder.Scanned}");
                    foreach (var file in folder.Files)
                        writer.WriteLine($"file;{Csv(file.FullPath)};{file.Bytes};true");
                    foreach (var child in folder.Folders.Values) stack.Push(child);
                }
            });
            StatusText.Text = "CSV сохранён: " + dialog.FileName;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка экспорта"); }
        finally { ExportButton.IsEnabled = true; }
    }

    private static string Csv(string text) => "\"" + text.Replace("\"", "\"\"")
        .Replace("\r", " ").Replace("\n", " ") + "\"";

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
