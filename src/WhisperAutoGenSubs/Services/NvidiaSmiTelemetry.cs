using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WhisperSelectGenSubs.Services;

/// <summary>
/// Reads NVIDIA's NVML driver library directly. NVIDIA documents NVML as the
/// library underneath nvidia-smi; nvidia-smi is used only for optional process VRAM.
/// </summary>
internal sealed class NvidiaTelemetryReader : IDisposable
{
    private static readonly Regex Number = new(@"-?\d+(?:[\.,]\d+)?", RegexOptions.Compiled);
    private readonly NvmlTelemetry _nvml;
    private readonly string? _smiPath;

    private NvidiaTelemetryReader(NvmlTelemetry nvml, string? smiPath)
    {
        _nvml = nvml;
        _smiPath = smiPath;
    }

    public static NvidiaTelemetryReader? TryCreate()
    {
        var nvml = NvmlTelemetry.TryCreate();
        return nvml is null ? null : new NvidiaTelemetryReader(nvml, FindSmiPath());
    }

    public async Task<NvidiaTelemetry?> ReadAsync(int workerProcessId, CancellationToken cancellationToken)
    {
        var card = _nvml.Read();
        if (card is null) return null;
        var processMemory = _smiPath is null ? null : await ReadProcessMemoryAsync(_smiPath, workerProcessId, cancellationToken);
        return card with { ProcessMemoryUsedMiB = processMemory };
    }

    public void Dispose() => _nvml.Dispose();

    private static string? FindSmiPath()
    {
        var systemDirectory = Environment.SystemDirectory;
        var programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
        var candidates = new[]
        {
            Path.Combine(systemDirectory, "nvidia-smi.exe"),
            Path.Combine(programW6432 ?? string.Empty, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)) ?? FindOnPath("nvidia-smi.exe");
    }

    private static string? FindOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue)) return null;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static async Task<int?> ReadProcessMemoryAsync(string executablePath, int workerProcessId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("--query-compute-apps=pid,used_gpu_memory");
        process.StartInfo.ArgumentList.Add("--format=csv,noheader,nounits");
        try
        {
            if (!process.Start()) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            await errorTask;
            if (process.ExitCode != 0) return null;
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fields = line.Split(',', StringSplitOptions.TrimEntries);
                if (fields.Length >= 2 && int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) && pid == workerProcessId)
                    return ParseInt(fields[1]);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        return null;
    }

    private static int? ParseInt(string value)
    {
        var match = Number.Match(value);
        if (!match.Success) return null;
        var normalized = match.Value.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? (int)Math.Round(parsed) : null;
    }
}

internal sealed class NvmlTelemetry : IDisposable
{
    private const int Success = 0;
    private readonly IntPtr _library;
    private readonly NvmlShutdown _shutdown;
    private readonly NvmlGetName _getName;
    private readonly NvmlGetUtilization _getUtilization;
    private readonly NvmlGetMemory _getMemory;
    private readonly NvmlGetTemperature _getTemperature;
    private readonly NvmlGetPower _getPower;
    private readonly IntPtr _device;
    private bool _disposed;

    private NvmlTelemetry(IntPtr library, NvmlShutdown shutdown, NvmlGetName getName, NvmlGetUtilization getUtilization, NvmlGetMemory getMemory, NvmlGetTemperature getTemperature, NvmlGetPower getPower, IntPtr device)
    {
        _library = library; _shutdown = shutdown; _getName = getName; _getUtilization = getUtilization;
        _getMemory = getMemory; _getTemperature = getTemperature; _getPower = getPower; _device = device;
    }

    public static NvmlTelemetry? TryCreate()
    {
        foreach (var path in CandidateDllPaths())
        {
            if (!NativeLibrary.TryLoad(path, out var library)) continue;
            try
            {
                var init = Export<NvmlInit>(library, "nvmlInit_v2");
                var shutdown = Export<NvmlShutdown>(library, "nvmlShutdown");
                var handle = Export<NvmlGetHandle>(library, "nvmlDeviceGetHandleByIndex_v2");
                var name = Export<NvmlGetName>(library, "nvmlDeviceGetName");
                var utilization = Export<NvmlGetUtilization>(library, "nvmlDeviceGetUtilizationRates");
                var memory = Export<NvmlGetMemory>(library, "nvmlDeviceGetMemoryInfo");
                var temperature = Export<NvmlGetTemperature>(library, "nvmlDeviceGetTemperature");
                var power = Export<NvmlGetPower>(library, "nvmlDeviceGetPowerUsage");
                if (init() != Success || handle(0, out var device) != Success || device == IntPtr.Zero)
                {
                    shutdown(); NativeLibrary.Free(library); continue;
                }
                return new NvmlTelemetry(library, shutdown, name, utilization, memory, temperature, power, device);
            }
            catch (EntryPointNotFoundException) { NativeLibrary.Free(library); }
        }
        return null;
    }

    public NvidiaTelemetry? Read()
    {
        if (_disposed) return null;
        var name = new StringBuilder(96);
        if (_getName(_device, name, (uint)name.Capacity) != Success) return null;
        var utilization = _getUtilization(_device, out var use) == Success ? use.Gpu : (uint?)null;
        var memory = _getMemory(_device, out var mem) == Success ? (int?)(mem.Used / 1024 / 1024) : null;
        var temperature = _getTemperature(_device, 0, out var temp) == Success ? (int?)temp : null;
        var power = _getPower(_device, out var milliwatts) == Success ? milliwatts / 1000d : (double?)null;
        return new NvidiaTelemetry(name.ToString(), utilization, memory, temperature, power, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _shutdown(); } catch { }
        NativeLibrary.Free(_library);
    }

    private static IEnumerable<string> CandidateDllPaths()
    {
        var programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvml.dll"),
            Path.Combine(programW6432 ?? string.Empty, "NVIDIA Corporation", "NVSMI", "nvml.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NVIDIA Corporation", "NVSMI", "nvml.dll")
        };
        return candidates.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static T Export<T>(IntPtr library, string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    [StructLayout(LayoutKind.Sequential)] private struct NvmlUtilizationRates { public uint Gpu; public uint Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct NvmlMemoryInfo { public ulong Total; public ulong Free; public ulong Used; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlShutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetHandle(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int NvmlGetName(IntPtr device, StringBuilder name, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetUtilization(IntPtr device, out NvmlUtilizationRates utilization);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetMemory(IntPtr device, out NvmlMemoryInfo memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetTemperature(IntPtr device, uint sensorType, out uint temperature);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetPower(IntPtr device, out uint milliwatts);
}
