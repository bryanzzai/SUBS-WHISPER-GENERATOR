using System.Runtime.InteropServices;
using System.Text;

namespace WhisperAutoGenSubs.Services;

/// <summary>
/// Reads the same Windows performance-counter family that Task Manager uses for GPU engines.
/// A missing counter is reported as null; the UI must never invent a GPU percentage.
/// </summary>
internal sealed class GpuUsageMonitor : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private readonly IntPtr _query;
    private readonly List<IntPtr> _counters = [];
    private bool _disposed;

    private GpuUsageMonitor(string wildcardPath)
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
            return;

        foreach (var path in ExpandPaths(wildcardPath))
        {
            if (PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var counter) == 0)
                _counters.Add(counter);
        }

        if (_counters.Count > 0)
            PdhCollectQueryData(_query); // Prime rate counters.
    }

    public static GpuUsageMonitor ForProcess(int processId) =>
        new($@"\GPU Engine(pid_{processId}_*)\Utilization Percentage");

    public static GpuUsageMonitor ForAllEngines() =>
        new(@"\GPU Engine(*)\Utilization Percentage");

    public double? ReadPercent()
    {
        if (_query == IntPtr.Zero || _counters.Count == 0 || PdhCollectQueryData(_query) != 0)
            return null;

        var highestEngine = double.NaN;
        foreach (var counter in _counters)
        {
            if (PdhGetFormattedCounterValue(counter, PdhFmtDouble, IntPtr.Zero, out var value) == 0 &&
                !double.IsNaN(value.DoubleValue) && !double.IsInfinity(value.DoubleValue))
            {
                highestEngine = double.IsNaN(highestEngine)
                    ? value.DoubleValue
                    : Math.Max(highestEngine, value.DoubleValue);
            }
        }

        return double.IsNaN(highestEngine) ? null : Math.Clamp(highestEngine, 0, 100);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_query != IntPtr.Zero)
            PdhCloseQuery(_query);
    }

    private static IEnumerable<string> ExpandPaths(string wildcardPath)
    {
        uint size = 0;
        var result = PdhExpandWildCardPath(null, wildcardPath, null, ref size, 0);
        if (result != PdhMoreData || size == 0)
            yield break;

        var buffer = new StringBuilder((int)size);
        if (PdhExpandWildCardPath(null, wildcardPath, buffer, ref size, 0) != 0)
            yield break;

        foreach (var path in buffer.ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries))
            yield return path;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhExpandWildCardPath(string? dataSource, string wildcardPath, StringBuilder? expandedPathList, ref uint pathListLength, uint flags);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PdhFormattedCounterValue value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }
}

internal sealed class SystemCpuMonitor
{
    private CpuTimes? _previous;

    public double ReadPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        var current = new CpuTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
        var previous = _previous;
        _previous = current;
        if (previous is null)
            return 0;

        var total = (current.Kernel - previous.Kernel) + (current.User - previous.User);
        var idleDelta = current.Idle - previous.Idle;
        return total == 0 ? 0 : Math.Clamp((total - idleDelta) * 100d / total, 0, 100);
    }

    private sealed record CpuTimes(ulong Idle, ulong Kernel, ulong User);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    private static ulong ToUInt64(FileTime time) => ((ulong)time.HighDateTime << 32) | time.LowDateTime;
}
