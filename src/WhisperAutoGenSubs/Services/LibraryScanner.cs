using WhisperAutoGenSubs.Models;

namespace WhisperAutoGenSubs.Services;

public sealed class LibraryScanner
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts"
    };

    public IReadOnlyList<VideoItem> Scan(string rootPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Library path is required.", nameof(rootPath));
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException(rootPath);

        var items = new List<VideoItem>();
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pending.Push(directory);
                }

                foreach (var file in Directory.EnumerateFiles(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!VideoExtensions.Contains(Path.GetExtension(file)))
                        continue;

                    items.Add(new VideoItem
                    {
                        FullPath = file,
                        FileName = Path.GetFileName(file),
                        Folder = current,
                        SubtitlePath = FindExistingSubtitle(file),
                        Status = "Ready"
                    });
                }
            }
            catch (UnauthorizedAccessException)
            {
                // An inaccessible folder must not stop a whole library scan.
            }
            catch (IOException)
            {
                // A temporarily unavailable folder must not stop a whole library scan.
            }
        }

        return items.OrderBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? FindExistingSubtitle(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath)!;
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        var standard = Path.Combine(directory, baseName + ".srt");
        return File.Exists(standard) ? standard : null;
    }
}
