using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace WhisperAutoGenSubs.Services;

/// <summary>
/// Reads the NVIDIA driver's own telemetry utility.  This is deliberately a
/// separate source from Windows' GPU Engine performance counters: the latter
/// can be localized and per-engine counters may be absent even while CUDA is
/// working.  No network connection or NVIDIA account is involved.
/// </summary>
internal sealed class NvidiaSmiTelemetry
{
    private static readonly Regex Number = new(@"-?\d+(?:[\.,]\d+)?", RegexOptions.Compiled);
    private readonly string _executablePath;

    private NvidiaSmiTelemetry(string executablePath) => _executablePath = executablePath;

    public static NvidiaSmiTelemetry? TryCreate()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        };

        var path = candidates.FirstOrDefault(File.Exists);
        return path is null ? null : new NvidiaSmiTelemetry(path);
    }

    public async Task<NvidiaTelemetry?> ReadAsync(int workerProcessId, CancellationToken cancellationToken)
    {
        // The first command is deliberately card-wide. NVIDIA defines this as
        // percent of the sample period in which kernels were executing.
        var gpuLine = await RunAsync(
            ["--query-gpu=name,utilization.gpu,memory.used,temperature.gpu,power.draw", "--format=csv,noheader,nounits"],
            cancellationToken);
        if (string.IsNullOrWhiteSpace(gpuLine))
            return null;

        // One line is returned per physical card. This app's meter describes
        // the first NVIDIA card, which is the selected CUDA device unless the
        // user has explicitly changed CUDA_VISIBLE_DEVICES outside the app.
        var firstGpuLine = gpuLine.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        var parts = firstGpuLine.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 5)
            return null;

        var processMemory = await ReadProcessMemoryAsync(workerProcessId, cancellationToken);
        return new NvidiaTelemetry(
            parts[0],
            ParseDouble(parts[1]),
            ParseInt(parts[2]),
            ParseInt(parts[3]),
            ParseDouble(parts[4]),
            processMemory);
    }

    private async Task<int?> ReadProcessMemoryAsync(int workerProcessId, CancellationToken cancellationToken)
    {
        // Some Windows driver modes do not expose compute-process rows.  That
        // is reported as unavailable, never represented as zero VRAM.
        var output = await RunAsync(
            ["--query-compute-apps=pid,used_gpu_memory", "--format=csv,noheader,nounits"],
            cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
            return null;

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length >= 2 && int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) && pid == workerProcessId)
                return ParseInt(fields[1]);
        }
        return null;
    }

    private async Task<string?> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start()) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            await errorTask;
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
        }
    }

    private static double? ParseDouble(string value)
    {
        var match = Number.Match(value);
        if (!match.Success) return null;
        var normalized = match.Value.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static int? ParseInt(string value)
    {
        var parsed = ParseDouble(value);
        return parsed is { } number ? (int)Math.Round(number) : null;
    }
}
