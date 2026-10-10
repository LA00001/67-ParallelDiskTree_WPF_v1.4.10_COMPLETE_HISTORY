using System.IO;
using System.Runtime.InteropServices;

namespace ParallelDiskTree;

// Reads real Windows logical-volume performance counters via PDH (no NuGet packages).
// These counters include ALL I/O to the selected volume, not just this scanner.
// % Disk Time может превышать 100; это счётчик Windows, а не процент физической загрузки устройства.
public sealed class WindowsDiskCounters : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private IntPtr _query, _busy, _latency, _bytes;
    private bool _valid;

    public bool Available => _valid;

    public WindowsDiskCounters(string root)
    {
        string? drive = Path.GetPathRoot(root);
        if (string.IsNullOrWhiteSpace(drive) || drive.Length < 2 || drive[1] != ':') return;
        string instance = char.ToUpperInvariant(drive[0]) + ":";
        try
        {
            if (PdhOpenQuery(IntPtr.Zero, IntPtr.Zero, out _query) != 0) return;
            string prefix = @"\LogicalDisk(" + instance + @")\";
            _busy = AddCounter(prefix + "% Disk Time");
            _latency = AddCounter(prefix + "Avg. Disk sec/Transfer");
            _bytes = AddCounter(prefix + "Disk Bytes/sec");
            _valid = _busy != IntPtr.Zero || _latency != IntPtr.Zero || _bytes != IntPtr.Zero;
            if (_valid) PdhCollectQueryData(_query); // Prime the rate counters.
            else Dispose();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Dispose();
        }
    }

    private IntPtr AddCounter(string name) =>
        PdhAddEnglishCounter(_query, name, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    public DiskSnapshot Read()
    {
        if (!_valid || PdhCollectQueryData(_query) != 0) return new(null, null, null);
        double? busy = Get(_busy);
        double? latencySeconds = Get(_latency);
        double? bytesPerSecond = Get(_bytes);
        return new(busy, latencySeconds * 1000, bytesPerSecond);
    }

    private static double? Get(IntPtr counter)
    {
        if (counter == IntPtr.Zero || PdhGetFormattedCounterValue(counter, PdhFmtDouble, IntPtr.Zero, out var value) != 0)
            return null;
        return value.CStatus is 0 or 1 && double.IsFinite(value.DoubleValue) && value.DoubleValue >= 0
            ? value.DoubleValue : null;
    }

    public void Dispose()
    {
        _valid = false;
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FormattedCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(IntPtr dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterValue")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out FormattedCounterValue value);
    [DllImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    private static extern uint PdhCloseQuery(IntPtr query);
}

public sealed record DiskSnapshot(double? BusyPercent, double? LatencyMilliseconds, double? BytesPerSecond);
