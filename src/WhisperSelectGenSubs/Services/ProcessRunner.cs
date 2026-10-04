using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace WhisperSelectGenSubs.Services;

public sealed record ProcessOutputLine(bool IsError, string Text);

public static class ProcessRunner
{
    public static async Task RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        IProgress<ProcessOutputLine>? output,
        IProgress<ProcessTelemetry>? telemetry,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {Path.GetFileName(executablePath)}.");

        TrySetLowPriority(process);
        var recentOutput = new ConcurrentQueue<string>();
        var stdoutTask = PumpAsync(process.StandardOutput, false, output, recentOutput);
        var stderrTask = PumpAsync(process.StandardError, true, output, recentOutput);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monitorTask = MonitorAsync(process, telemetry, monitorCancellation.Token);

        using var registration = cancellationToken.Register(() => KillIfRunning(process));
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            monitorCancellation.Cancel();
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        try { await monitorTask; } catch (OperationCanceledException) { }

        if (process.ExitCode != 0)
        {
            var detail = string.Join(Environment.NewLine, recentOutput);
            throw new InvalidOperationException(
                $"{Path.GetFileName(executablePath)} failed with exit code {process.ExitCode}. {detail.Trim()}");
        }
    }

    private static async Task PumpAsync(StreamReader reader, bool isError, IProgress<ProcessOutputLine>? output, ConcurrentQueue<string> recentOutput)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (recentOutput.Count >= 40)
                recentOutput.TryDequeue(out _);
            recentOutput.Enqueue(line);
            output?.Report(new ProcessOutputLine(isError, line));
        }
    }

    private static async Task MonitorAsync(Process process, IProgress<ProcessTelemetry>? telemetry, CancellationToken cancellationToken)
    {
        if (telemetry is null)
            return;

        using var ownGpu = GpuUsageMonitor.ForProcess(process.Id);
        using var totalGpu = GpuUsageMonitor.ForAllEngines();
        using var nvidia = NvidiaTelemetryReader.TryCreate();
        var systemCpu = new SystemCpuMonitor();
        var stopwatch = Stopwatch.StartNew();
        var previousWall = stopwatch.Elapsed;
        var previousCpu = process.TotalProcessorTime;

        while (!cancellationToken.IsCancellationRequested && !process.HasExited)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            if (process.HasExited)
                break;

            process.Refresh();
            var now = stopwatch.Elapsed;
            var cpuNow = process.TotalProcessorTime;
            var wallSeconds = Math.Max(0.001, (now - previousWall).TotalSeconds);
            var processCpu = Math.Clamp(
                (cpuNow - previousCpu).TotalSeconds / wallSeconds / Environment.ProcessorCount * 100d,
                0, 100);

            previousWall = now;
            previousCpu = cpuNow;
            var nvidiaSample = nvidia is null ? null : await nvidia.ReadAsync(process.Id, cancellationToken);
            telemetry.Report(new ProcessTelemetry(processCpu, systemCpu.ReadPercent(), ownGpu.ReadPercent(), totalGpu.ReadPercent(), nvidiaSample, nvidia is not null));
        }
    }

    private static void TrySetLowPriority(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }
}
