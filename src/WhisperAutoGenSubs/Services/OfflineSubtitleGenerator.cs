using System.IO;

namespace WhisperAutoGenSubs.Services;

public sealed class OfflineSubtitleGenerator
{
    public static int WorkerThreadCount => Math.Max(1, (int)Math.Floor(Environment.ProcessorCount * 0.75));

    public async Task<string> GenerateAsync(
        string videoPath,
        string ffmpegPath,
        string whisperCliPath,
        string modelPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
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
        var threadCount = WorkerThreadCount.ToString();

        try
        {
            progress?.Report("Extracting audio…");
            await ProcessRunner.RunAsync(ffmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-threads", threadCount,
                "-i", videoPath,
                "-map", "0:a:0?",
                "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
                audioPath
            ], cancellationToken);

            if (!File.Exists(audioPath))
                throw new InvalidOperationException("FFmpeg did not create an audio track for this video.");

            progress?.Report("Transcribing English audio locally…");
            await ProcessRunner.RunAsync(whisperCliPath,
            [
                "--model", modelPath,
                "--file", audioPath,
                "--language", "en",
                "--threads", threadCount,
                "--output-srt",
                "--output-file", outputBase,
                "--no-prints"
            ], cancellationToken);

            if (!File.Exists(generatedSrt))
                throw new InvalidOperationException("whisper.cpp completed without producing an SRT file.");

            progress?.Report("Saving SRT…");
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

    private static void ValidateExecutable(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"Choose a valid {name} executable first.", path);
    }
}
