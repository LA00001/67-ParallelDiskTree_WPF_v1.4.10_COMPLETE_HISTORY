using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace ParallelDiskTree;

public partial class PerformanceWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private IReadOnlyList<HistoricalRun> _runs = Array.Empty<HistoricalRun>();

    public PerformanceWindow()
    {
        InitializeComponent();
        FitWindow();
        Loaded += (_, _) => Reload();
    }

    private void FitWindow()
    {
        Rect area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0) return;
        double w = Math.Max(1, area.Width - 24);
        double h = Math.Max(1, area.Height - 24);
        MinWidth = Math.Min(MinWidth, w);
        MinHeight = Math.Min(MinHeight, h);
        Width = Math.Min(1170, w);
        Height = Math.Min(810, h);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private void Reload()
    {
        _runs = PerformanceHistory.BestObserved();
        RecordsGrid.ItemsSource = _runs;
        if (_runs.Count == 0) return;
        var fastest = _runs.Where(x => !x.IsBaseline).OrderBy(x => x.Seconds).FirstOrDefault();
        if (fastest is null) return;
        RecordText.Text = fastest.Seconds.ToString("N3", Ru) + " с";
        RecordSub.Text = $"{fastest.ModeLabel} • {fastest.ObjectsPerSecond.ToString("N0", Ru)} объектов/с";
        ObjectsText.Text = fastest.Objects.ToString("N0", Ru);
        VolumeText.Text = fastest.Bytes.HasValue
            ? (fastest.Bytes.Value / (1024d * 1024 * 1024)).ToString("N2", Ru) + " GiB" : "—";
        SpeedupText.Text = (PerformanceHistory.BaselineMilliseconds / (fastest.Seconds * 1000d)).ToString("N2", Ru) + "×";
        DrawChart();
    }

    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(43, 59, 88));
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(140, 168, 202));
    private static readonly Brush CyanBrush = new SolidColorBrush(Color.FromRgb(0, 245, 233));
    private static readonly Brush PinkBrush = new SolidColorBrush(Color.FromRgb(255, 71, 189));

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();
        double w = ChartCanvas.ActualWidth, h = ChartCanvas.ActualHeight;
        // Include the real first sequential baseline as the N=1 chart point.
        // A later best sequential pass is intentionally not a separate category.
        var chartRuns = _runs.ToArray();
        if (w < 400 || h < 200 || chartRuns.Length == 0) return;
        const double left = 53, right = 27, top = 25, bottom = 49;
        double plotW = Math.Max(100, w - left - right);
        double plotH = h - top - bottom;
        // Focused, explicitly labeled y-axis: every point is still plotted to scale.
        // Leave some space below the best and above the worst observed value.
        double minTime = Math.Max(0, Math.Floor((chartRuns.Min(x => x.Seconds) - 4) / 10.0) * 10);
        double maxTime = Math.Ceiling((chartRuns.Max(x => x.Seconds) + 6) / 10.0) * 10;
        if (maxTime - minTime < 40) maxTime = minTime + 40;
        double Y(double seconds) => top + (maxTime - seconds) / (maxTime - minTime) * plotH;
        // Lower times appear toward the bottom: draw descending delay curve without
        // implying that a taller bar denotes faster throughput.
        // Data point labels + vertical neon guides give unambiguous exact values.
        for (int tick = 0; tick <= 4; tick++)
        {
            double value = minTime + (maxTime - minTime) * tick / 4;
            double y = Y(value);
            AddLine(left, y, w - right, y, GridBrush, 1, tick == 0 ? 0.8 : 0.5);
            AddText(value.ToString("N0", Ru), 4, y - 9, DimBrush, 11, 45, TextAlignment.Right);
        }
        var pts = new List<Point>();
        var count = chartRuns.Length;
        for (int i = 0; i < count; i++)
        {
            var run = chartRuns[i];
            double x = left + (i + 0.5) * plotW / count;
            double y = Y(run.Seconds);
            bool record = run.Seconds == chartRuns.Min(r => r.Seconds);
            Brush ink = record ? PinkBrush : CyanBrush;
            AddLine(x, top, x, top + plotH, GridBrush, 1, 0.65);
            AddLine(x, y, x, top + plotH, ink, record ? 3 : 2, record ? 0.7 : 0.3);
            AddText(run.ChartLabel, x - 34, top + plotH + 16,
                run.IsAdaptive ? PinkBrush : DimBrush, 12, 68, TextAlignment.Center, true);
            pts.Add(new Point(x, y));
        }
        // Only connect fixed-N points. Adaptive runs are separate categories,
        // not an extra numerical worker count on the fixed-N curve.
        var fixedPoints = pts.TakeWhile((_, i) => !chartRuns[i].IsAdaptive).ToList();
        var under = new Polyline
        {
            Points = new PointCollection(fixedPoints),
            Stroke = new SolidColorBrush(Color.FromRgb(0, 80, 120)),
            StrokeThickness = 11,
            Opacity = 0.4,
            StrokeLineJoin = PenLineJoin.Round
        };
        ChartCanvas.Children.Add(under);
        var line = new Polyline
        {
            Points = new PointCollection(fixedPoints), Stroke = CyanBrush, StrokeThickness = 3.5,
            StrokeLineJoin = PenLineJoin.Round,
            Effect = new DropShadowEffect { Color = Colors.Cyan, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 }
        };
        ChartCanvas.Children.Add(line);
        foreach (var (p, index) in pts.Select((p, i) => (p, i)))
        {
            var run = chartRuns[index];
            bool record = run.Seconds == chartRuns.Min(r => r.Seconds);
            Brush ink = record ? PinkBrush : CyanBrush;
            var dot = new Ellipse
            {
                Width = record ? 15 : 11, Height = record ? 15 : 11,
                Fill = ink, Stroke = Brushes.White, StrokeThickness = 1,
                Effect = new DropShadowEffect
                {
                    Color = record ? Color.FromRgb(255, 71, 189) : Colors.Cyan,
                    BlurRadius = 16, ShadowDepth = 0, Opacity = 1
                },
                ToolTip = $"{run.ModeLabel} • {run.Seconds.ToString("N3", Ru)} с • {run.ObjectsPerSecond.ToString("N0", Ru)} объектов/с"
            };
            Canvas.SetLeft(dot, p.X - dot.Width / 2);
            Canvas.SetTop(dot, p.Y - dot.Height / 2);
            ChartCanvas.Children.Add(dot);
            double labelY = p.Y - 29;
            if (labelY < top) labelY = p.Y + 12;
            AddText(run.Seconds.ToString("N3", Ru) + " с", p.X - 46, labelY, ink, 12, 92, TextAlignment.Center, record);
        }
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush brush,
        double thickness, double opacity)
    {
        ChartCanvas.Children.Add(new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush,
            StrokeThickness = thickness, Opacity = opacity
        });
    }

    private void AddText(string s, double x, double y, Brush ink, double font,
        double width, TextAlignment alignment, bool bold = false)
    {
        var label = new TextBlock
        {
            Text = s, Foreground = ink, FontSize = font, Width = width,
            TextAlignment = alignment, FontFamily = new FontFamily("Consolas"),
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        ChartCanvas.Children.Add(label);
    }
}
