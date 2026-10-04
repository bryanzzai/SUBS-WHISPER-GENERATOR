namespace WhisperSelectGenSubs.Configuration;

public sealed record AppSettings(
    string LibraryRoot,
    string FfmpegPath,
    string WhisperCliPath,
    string ModelPath)
{
    public static AppSettings Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);
}
