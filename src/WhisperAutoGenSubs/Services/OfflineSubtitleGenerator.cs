using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace WhisperAutoGenSubs.Services;

public sealed class OfflineSubtitleGenerator
{
    private static readonly Regex WhisperProgress = new(@"progress\s*=\s*(?<percent>\d{1,3})%", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<string> GenerateAsync(string videoPath, string ffmpegPath, string whisperCliPath, string modelPath,
        IProgress<GenerationProgress>? progress, CancellationToken cancellationToken)
    {
        ValidateExecutable(ffmpegPath, "FFmpeg");
        ValidateExecutable(whisperCliPath, "whisper.cpp CLI");
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("The selected whisper.cpp model does not exist.", modelPath);

        var workspace = Path.Combine(Path.GetTempPath(), "WhisperAutoGenSubs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var audioPath = Path.Combine(workspace, "audio.wav");
        var outputBase = Path.Combine(workspace, "subtitle");
        var generatedSrt = outputBase + ".srt";
        var destinationSrt = Path.ChangeExtension(videoPath, ".srt");
        try
        {
            var duration = await TryGetDurationAsync(ffmpegPath, videoPath, cancellationToken);
            await ExtractAudioAsync(videoPath, ffmpegPath, audioPath, duration, progress, cancellationToken);
            if (!File.Exists(audioPath))
                throw new InvalidOperationException("FFmpeg did not create an audio track for this video.");

            await TranscribeAsync(whisperCliPath, modelPath, audioPath, outputBase, duration, progress, cancellationToken);
            if (!File.Exists(generatedSrt))
                throw new InvalidOperationException("whisper.cpp completed without producing an SRT file.");

            progress?.Report(new GenerationProgress("Saving final SRT…", 100));
            File.Move(generatedSrt, destinationSrt, overwrite: false);
            return destinationSrt;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workspace))
                    Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
                // Temporary files are harmless if an external process still holds one.
            }
        }
    }

    private static async Task ExtractAudioAsync(string videoPath, string ffmpegPath, string audioPath, TimeSpan? duration,
        IProgress<GenerationProgress>? progress, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        ProcessTelemetry? latestTelemetry = null;
        double? percent = duration is null ? null : 0;
        var telemetry = new Progress<ProcessTelemetry>(sample =>
        {
            latestTelemetry = sample;
            progress?.Report(new GenerationProgress("Extracting audio…", percent, null, null, "FFmpeg", sample));
        });
        var output = new Progress<ProcessOutputLine>(line =>
        {
            if (!TryReadFfmpegTime(line.Text, out var completed) || duration is not { } total || total <= TimeSpan.Zero)
                return;

            var currentPercent = Math.Clamp(completed.TotalMilliseconds / total.TotalMilliseconds * 100d, 0, 100);
            percent = currentPercent;
            progress?.Report(new GenerationProgress("Extracting audio…", currentPercent, EstimateRemaining(started.Elapsed, currentPercent),
                completed.TotalSeconds / Math.Max(0.01, started.Elapsed.TotalSeconds), "FFmpeg", latestTelemetry));
        });

        progress?.Report(new GenerationProgress(duration is null ? "Extracting audio… (duration unavailable)" : "Extracting audio…",
            percent, null, null, "FFmpeg"));
        var threads = Math.Max(1, Environment.ProcessorCount).ToString(CultureInfo.InvariantCulture);
        await ProcessRunner.RunAsync(ffmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-progress", "pipe:1", "-nostats", "-stats_period", "0.5",
            "-threads", threads, "-filter_threads", threads,
            "-i", videoPath, "-map", "0:a:0?", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", audioPath
        ], output, telemetry, cancellationToken);
    }

    private static async Task TranscribeAsync(string whisperCliPath, string modelPath, string audioPath, string outputBase, TimeSpan? duration,
        IProgress<GenerationProgress>? progress, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        ProcessTelemetry? latestTelemetry = null;
        var engine = "Starting Whisper — backend being detected…";
        var percent = 0d;
        void Report()
        {
            double? speed = duration is null || percent <= 0
                ? null
                : duration.Value.TotalSeconds * percent / 100d / Math.Max(0.01, started.Elapsed.TotalSeconds);
            progress?.Report(new GenerationProgress("Transcribing English audio…", percent <= 0 ? null : percent,
                EstimateRemaining(started.Elapsed, percent), speed, engine, latestTelemetry));
        }

        var telemetry = new Progress<ProcessTelemetry>(sample => { latestTelemetry = sample; Report(); });
        var output = new Progress<ProcessOutputLine>(line =>
        {
            var detected = DetectWhisperEngine(line.Text);
            if (detected is not null)
                engine = detected;

            var match = WhisperProgress.Match(line.Text);
            if (match.Success && double.TryParse(match.Groups["percent"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                percent = Math.Clamp(value, 0, 100);
            if (detected is not null || match.Success)
                Report();
        });

        Report();
        await ProcessRunner.RunAsync(whisperCliPath,
        [
            "--model", modelPath, "--file", audioPath, "--language", "en",
            "--threads", Math.Max(1, Environment.ProcessorCount).ToString(CultureInfo.InvariantCulture),
            "--output-srt", "--output-file", outputBase, "--print-progress"
        ], output, telemetry, cancellationToken);
    }

    private static async Task<TimeSpan?> TryGetDurationAsync(string ffmpegPath, string videoPath, CancellationToken cancellationToken)
    {
        var ffprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? string.Empty, "ffprobe.exe");
        if (!File.Exists(ffprobePath)) return null;

        TimeSpan? result = null;
        var output = new Progress<ProcessOutputLine>(line =>
        {
            if (!line.IsError && double.TryParse(line.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                result = TimeSpan.FromSeconds(seconds);
        });
        try
        {
            await ProcessRunner.RunAsync(ffprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", videoPath],
            output, null, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // No duration means no exact FFmpeg percentage, not a failed subtitle job.
        }
        return result;
    }

    private static bool TryReadFfmpegTime(string line, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        const string prefix = "out_time=";
        return line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               TimeSpan.TryParse(line[prefix.Length..], CultureInfo.InvariantCulture, out time);
    }

    private static string? DetectWhisperEngine(string line)
    {
        var text = line.ToLowerInvariant();
        if (text.Contains("cuda")) return "CUDA GPU active";
        if (text.Contains("vulkan")) return "Vulkan GPU active";
        if (text.Contains("use gpu = 1")) return "GPU active";
        if (text.Contains("use gpu = 0")) return "CPU-only executable";
        return null;
    }

    private static TimeSpan? EstimateRemaining(TimeSpan elapsed, double percent)
    {
        if (percent < 5 || percent >= 100 || elapsed < TimeSpan.FromSeconds(2)) return null;
        return TimeSpan.FromTicks((long)(elapsed.Ticks * (100d - percent) / percent));
    }

    private static void ValidateExecutable(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"Choose a valid {name} executable first.", path);
    }
}
