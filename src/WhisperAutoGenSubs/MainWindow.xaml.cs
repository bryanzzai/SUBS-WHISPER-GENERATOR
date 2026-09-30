using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using WhisperAutoGenSubs.Configuration;
using WhisperAutoGenSubs.Models;
using WhisperAutoGenSubs.Services;

namespace WhisperAutoGenSubs;

public partial class MainWindow : Window
{
    private readonly LibraryScanner _scanner = new();
    private readonly OfflineSubtitleGenerator _generator = new();
    private CancellationTokenSource? _operationCts;

    public ObservableCollection<VideoItem> Videos { get; } = [];
    public string ReleaseLabel => $"Release {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown"}";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        var settings = AppSettingsStore.Load();
        LibraryPathTextBox.Text = settings.LibraryRoot;
        FfmpegPathTextBox.Text = settings.FfmpegPath;
        WhisperCliPathTextBox.Text = settings.WhisperCliPath;
        ModelPathTextBox.Text = settings.ModelPath;
    }

    private void BrowseLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose video library", Multiselect = false };
        if (Directory.Exists(LibraryPathTextBox.Text))
            dialog.InitialDirectory = LibraryPathTextBox.Text;
        if (dialog.ShowDialog(this) == true)
            LibraryPathTextBox.Text = dialog.FolderName;
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e) => PickInto(FfmpegPathTextBox, "Choose ffmpeg.exe", "ffmpeg.exe");
    private void BrowseWhisperCli_Click(object sender, RoutedEventArgs e) => PickInto(WhisperCliPathTextBox, "Choose whisper-cli.exe", "whisper-cli.exe");
    private void BrowseModel_Click(object sender, RoutedEventArgs e) => PickInto(ModelPathTextBox, "Choose local English Whisper model", "*.bin;*.gguf");

    private void PickInto(System.Windows.Controls.TextBox target, string title, string filter)
    {
        var file = PickFile(title, filter);
        if (!string.IsNullOrWhiteSpace(file))
            target.Text = file;
    }

    private string PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = $"Supported files|{filter}|All files|*.*", CheckFileExists = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : string.Empty;
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        var root = LibraryPathTextBox.Text.Trim();
        if (!Directory.Exists(root))
        {
            ShowInfo("Choose a valid video library first.");
            return;
        }

        BeginOperation("Scanning video library…");
        try
        {
            var items = await Task.Run(() => _scanner.Scan(root, _operationCts!.Token), _operationCts!.Token);
            Videos.Clear();
            foreach (var item in items)
                Videos.Add(item);

            var missing = Videos.Count(x => !x.HasSubtitle);
            StatusTextBlock.Text = $"Scan complete. {Videos.Count} videos; {missing} need English subtitles.";
            CountTextBlock.Text = $"{Videos.Count} videos";
            ProgressBar.Maximum = Math.Max(1, Videos.Count);
            ProgressBar.Value = Videos.Count;
            SaveSettings();
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            ShowError("Scan failed", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        var missing = Videos.Where(x => !x.HasSubtitle).ToArray();
        if (missing.Length == 0)
        {
            ShowInfo("Scan a library first, or all scanned videos already have a matching .srt file.");
            return;
        }

        var ffmpeg = FfmpegPathTextBox.Text.Trim();
        var whisper = WhisperCliPathTextBox.Text.Trim();
        var model = ModelPathTextBox.Text.Trim();
        if (!File.Exists(ffmpeg) || !File.Exists(whisper) || !File.Exists(model))
        {
            ShowInfo("Choose valid local paths for ffmpeg.exe, whisper-cli.exe, and an English Whisper model.");
            return;
        }

        SaveSettings();
        BeginOperation($"Preparing GPU/CPU instrument panel for {missing.Length} video(s)…");
        ProgressBar.Maximum = missing.Length;
        ProgressBar.Value = 0;
        ProgressBar.IsIndeterminate = false;
        ResetDashboard();
        var failures = new List<string>();

        try
        {
            for (var index = 0; index < missing.Length; index++)
            {
                var item = missing[index];
                _operationCts!.Token.ThrowIfCancellationRequested();
                item.Status = "Preparing…";
                StatusTextBlock.Text = $"Generating {index + 1}/{missing.Length}: {item.FileName}";
                CurrentFileTextBlock.Text = $"{index + 1}/{missing.Length} — {item.FileName}";

                var phaseProgress = new Progress<GenerationProgress>(update =>
                {
                    UpdateDashboard(update, item, index, missing.Length);
                });

                try
                {
                    item.SubtitlePath = await _generator.GenerateAsync(
                        item.FullPath, ffmpeg, whisper, model, phaseProgress, _operationCts.Token);
                    item.Status = "Generated.";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    item.Status = "Failed: " + ex.Message;
                    failures.Add(item.FullPath);
                }

                ProgressBar.Value = index + 1;
            }

            WriteFailureReport(LibraryPathTextBox.Text.Trim(), failures);
            StatusTextBlock.Text = $"Finished. {missing.Length - failures.Count} generated; {failures.Count} failed.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Generation cancelled.";
        }
        catch (Exception ex)
        {
            ShowError("Subtitle generation failed", ex);
        }
        finally
        {
            ProgressBar.IsIndeterminate = false;
            FileProgressBar.IsIndeterminate = false;
            EndOperation();
        }
    }

    private void SaveSettings() => AppSettingsStore.Save(new AppSettings(
        LibraryPathTextBox.Text.Trim(),
        FfmpegPathTextBox.Text.Trim(),
        WhisperCliPathTextBox.Text.Trim(),
        ModelPathTextBox.Text.Trim()));

    private static void WriteFailureReport(string libraryRoot, IReadOnlyCollection<string> failures)
    {
        var report = Path.Combine(libraryRoot, "Failed-Subtitle-Generation.txt");
        if (failures.Count == 0)
        {
            if (File.Exists(report)) File.Delete(report);
            return;
        }

        var lines = new[] { "Whisper Auto Gen Subs - failures", $"Generated: {DateTimeOffset.Now:O}", "" }.Concat(failures);
        File.WriteAllLines(report, lines);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private void BeginOperation(string status)
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        GenerateButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusTextBlock.Text = status;
    }

    private void ResetDashboard()
    {
        CurrentFileTextBlock.Text = "No file is being processed.";
        PhaseTextBlock.Text = "Waiting";
        FileProgressBar.IsIndeterminate = false;
        FileProgressBar.Value = 0;
        FilePercentTextBlock.Text = "—";
        EtaTextBlock.Text = "Waiting for measurement…";
        SpeedTextBlock.Text = "—";
        EngineTextBlock.Text = "Not started";
        WorkerCpuTextBlock.Text = "Active process CPU: —";
        SystemCpuTextBlock.Text = "Whole PC CPU: —";
        WorkerGpuTextBlock.Text = "NVIDIA GPU: waiting for driver telemetry…";
        TotalGpuTextBlock.Text = "Whisper GPU memory: —";
        WorkerCpuBar.Value = 0;
        WorkerGpuBar.Value = 0;
    }

    private void UpdateDashboard(GenerationProgress update, VideoItem item, int index, int total)
    {
        var percent = update.Percent;
        item.Status = percent is { } value ? $"{update.Phase} {value:0}%" : update.Phase;
        StatusTextBlock.Text = $"{update.Phase} — {index + 1}/{total}: {item.FileName}";
        PhaseTextBlock.Text = update.Phase;
        CurrentFileTextBlock.Text = $"{index + 1}/{total} — {item.FileName}";

        FileProgressBar.IsIndeterminate = percent is null;
        if (percent is { } progressPercent)
        {
            FileProgressBar.Value = progressPercent;
            FilePercentTextBlock.Text = $"{progressPercent:0}%";
            ProgressBar.Value = index + progressPercent / 100d;
        }
        else
        {
            FilePercentTextBlock.Text = "Measuring…";
            ProgressBar.Value = index;
        }

        EtaTextBlock.Text = update.EstimatedRemaining is { } eta
            ? "About " + FormatDuration(eta) + " left"
            : percent is >= 5 and < 100 ? "Calculating…" : "Waiting for measurement…";
        SpeedTextBlock.Text = update.RealtimeFactor is { } speed ? $"{speed:0.0}× real time" : "—";
        if (!string.IsNullOrWhiteSpace(update.Engine))
            EngineTextBlock.Text = update.Engine;

        if (update.Telemetry is { } telemetry)
        {
            WorkerCpuBar.Value = telemetry.ProcessCpuPercent;
            WorkerCpuTextBlock.Text = $"Active process CPU: {telemetry.ProcessCpuPercent:0}%";
            SystemCpuTextBlock.Text = $"Whole PC CPU: {telemetry.SystemCpuPercent:0}%";
            if (telemetry.Nvidia is { } nvidia)
            {
                WorkerGpuBar.Value = nvidia.GpuUtilizationPercent ?? 0;
                var temperature = nvidia.TemperatureC is { } temp ? $" · {temp}°C" : string.Empty;
                var power = nvidia.PowerWatts is { } watts ? $" · {watts:0} W" : string.Empty;
                var memory = nvidia.MemoryUsedMiB is { } used ? $" · {used:N0} MiB used" : string.Empty;
                WorkerGpuTextBlock.Text = nvidia.GpuUtilizationPercent is { } gpu
                    ? $"{nvidia.GpuName}: {gpu:0}% (NVIDIA driver){temperature}{power}"
                    : $"{nvidia.GpuName}: utilization unavailable from NVIDIA driver";
                TotalGpuTextBlock.Text = nvidia.ProcessMemoryUsedMiB is { } processMemory
                    ? $"Whisper GPU memory: {processMemory:N0} MiB (matched to whisper-cli){memory}"
                    : $"Whisper GPU memory: not reported for this process{memory}";
            }
            else
            {
                WorkerGpuBar.Value = telemetry.TotalGpuPercent ?? telemetry.ProcessGpuPercent ?? 0;
                WorkerGpuTextBlock.Text = telemetry.TotalGpuPercent is { } totalGpu
                    ? $"GPU (Windows counter): {totalGpu:0}%"
                    : telemetry.NvidiaDriverDetected
                        ? "GPU meter: NVIDIA driver telemetry returned no sample"
                        : "GPU meter: NVIDIA driver telemetry not found; Windows counter unavailable";
                TotalGpuTextBlock.Text = telemetry.ProcessGpuPercent is { } gpu
                    ? $"Whisper GPU engine (Windows counter): {gpu:0}%"
                    : telemetry.NvidiaDriverDetected
                        ? "Whisper GPU memory: NVIDIA driver query returned no sample"
                        : "Whisper GPU memory: unavailable without NVIDIA driver telemetry";
            }
        }
    }

    private static string FormatDuration(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
        : $"{time.Minutes}:{time.Seconds:00}";

    private void EndOperation()
    {
        ScanButton.IsEnabled = true;
        GenerateButton.IsEnabled = true;
        CancelButton.IsEnabled = false;
        _operationCts?.Dispose();
        _operationCts = null;
    }

    private void ShowInfo(string message) => MessageBox.Show(this, message, "Whisper Auto Gen Subs", MessageBoxButton.OK, MessageBoxImage.Information);
    private void ShowError(string title, Exception ex)
    {
        StatusTextBlock.Text = title + ".";
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
