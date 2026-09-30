using System.Collections.ObjectModel;
using System.IO;
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
        BeginOperation($"Generating 0/{missing.Length}…");
        ProgressBar.Maximum = missing.Length;
        ProgressBar.Value = 0;
        var failures = new List<string>();

        try
        {
            for (var index = 0; index < missing.Length; index++)
            {
                var item = missing[index];
                _operationCts!.Token.ThrowIfCancellationRequested();
                item.Status = "Generating English SRT…";
                StatusTextBlock.Text = $"Generating {index + 1}/{missing.Length}: {item.FileName}";

                try
                {
                    item.SubtitlePath = await _generator.GenerateAsync(item.FullPath, ffmpeg, whisper, model, _operationCts.Token);
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
