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
    private const uint PdhRefreshCounters = 0x00000001;
    private readonly string _englishWildcardPath;
    private IntPtr _query;
    private readonly List<IntPtr> _counters = [];
    private int _readsSinceRefresh;
    private int _zeroReads;
    private bool _disposed;

    private GpuUsageMonitor(string englishWildcardPath)
    {
        _englishWildcardPath = englishWildcardPath;
        RebindCounters();
    }

    public static GpuUsageMonitor ForProcess(int processId) =>
        new($@"\GPU Engine(pid_{processId}_*)\Utilization Percentage");

    public static GpuUsageMonitor ForAllEngines() =>
        new(@"\GPU Engine(*)\Utilization Percentage");

    public double? ReadPercent()
    {
        if (_disposed)
            return null;

        // Windows often creates the per-PID CUDA engine only after the first GPU submission.
        // Rebind until it appears; rate counters then need one fresh interval.
        if (_counters.Count == 0)
        {
            if (++_readsSinceRefresh >= 2)
                RebindCounters();
            return null; // Rate counters need one interval after being rebound.
        }

        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0)
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

        if (double.IsNaN(highestEngine))
            return null;

        var result = Math.Clamp(highestEngine, 0, 100);
        // A CUDA engine can be registered a moment after a non-working process entry.
        // Refresh that stale list after several consecutive zero samples, not on every tick.
        if (result < 0.05 && ++_zeroReads >= 3)
        {
            RebindCounters();
            return null;
        }

        _zeroReads = 0;
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseQuery();
    }

    private void RebindCounters()
    {
        CloseQuery();
        _counters.Clear();
        _readsSinceRefresh = 0;
        _zeroReads = 0;
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
            return;

        // PdhExpandWildCardPath accepts localized paths.  It is tempting to send
        // "GPU Engine" straight to it, but that silently finds no counters on a
        // non-English Windows installation.  Microsoft specifies this sequence:
        // Add English wildcard -> get localized path -> expand -> add localized paths.
        foreach (var path in ExpandEnglishPaths(_query, _englishWildcardPath))
        {
            if (PdhAddCounter(_query, path, IntPtr.Zero, out var counter) == 0)
                _counters.Add(counter);
        }

        if (_counters.Count > 0)
            PdhCollectQueryData(_query); // Prime rate counters.
    }

    private void CloseQuery()
    {
        if (_query == IntPtr.Zero) return;
        PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    private static IEnumerable<string> ExpandEnglishPaths(IntPtr query, string englishWildcardPath)
    {
        if (PdhAddEnglishCounter(query, englishWildcardPath, IntPtr.Zero, out var englishWildcardCounter) != 0)
            yield break;

        try
        {
            var localizedWildcardPath = GetLocalizedPath(englishWildcardCounter);
            if (string.IsNullOrWhiteSpace(localizedWildcardPath))
                yield break;

            foreach (var path in ExpandLocalizedPaths(localizedWildcardPath))
                yield return path;
        }
        finally
        {
            PdhRemoveCounter(englishWildcardCounter);
        }
    }

    private static string? GetLocalizedPath(IntPtr counter)
    {
        uint size = 0;
        var result = PdhGetCounterInfo(counter, false, ref size, IntPtr.Zero);
        if (result != PdhMoreData || size == 0)
            return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetCounterInfo(counter, false, ref size, buffer) != 0)
                return null;

            // PDH_COUNTER_INFO_W: six DWORD values, two DWORD_PTR values, then szFullPath.
            var fullPathOffset = sizeof(uint) * 6 + IntPtr.Size * 2;
            var fullPath = Marshal.ReadIntPtr(buffer, fullPathOffset);
            return fullPath == IntPtr.Zero ? null : Marshal.PtrToStringUni(fullPath);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IEnumerable<string> ExpandLocalizedPaths(string wildcardPath)
    {
        uint size = 0;
        var result = PdhExpandWildCardPath(null, wildcardPath, null, ref size, PdhRefreshCounters);
        if (result != PdhMoreData || size == 0)
            yield break;

        var buffer = new StringBuilder((int)size);
        if (PdhExpandWildCardPath(null, wildcardPath, buffer, ref size, PdhRefreshCounters) != 0)
            yield break;

        foreach (var path in buffer.ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries))
            yield return path;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhRemoveCounter(IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhExpandWildCardPath(string? dataSource, string wildcardPath, StringBuilder? expandedPathList, ref uint pathListLength, uint flags);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetCounterInfo(IntPtr counter, [MarshalAs(UnmanagedType.Bool)] bool retrieveExplainText, ref uint bufferSize, IntPtr buffer);

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
