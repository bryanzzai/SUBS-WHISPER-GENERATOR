using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace WhisperAutoGenSubs.Models;

public sealed class VideoItem : INotifyPropertyChanged
{
    private string _status = "Ready";
    private string? _subtitlePath;

    public required string FullPath { get; init; }
    public required string FileName { get; init; }
    public required string Folder { get; init; }

    public string? SubtitlePath
    {
        get => _subtitlePath;
        set
        {
            if (_subtitlePath == value) return;
            _subtitlePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSubtitle));
            OnPropertyChanged(nameof(SubtitleDisplay));
        }
    }

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(SubtitlePath);

    public string SubtitleDisplay => HasSubtitle ? Path.GetFileName(SubtitlePath) : "Missing";

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

