# Whisper Auto Gen Subs

Windows desktop app that recursively scans a local video library and generates an English `.srt` sidecar next to every video that does not already have one.

It is deliberately offline at runtime. It does not call OpenSubtitles, any subtitle site, OpenAI, or any other web service.

## What it does

```text
Silo S03E01.mkv  ->  Silo S03E01.srt
```

1. Choose the root of a video library.
2. Choose local copies of `ffmpeg.exe`, `whisper-cli.exe`, and an English whisper.cpp model.
3. Scan.
4. Click **Generate missing subtitles**.

The app extracts the first audio track into a temporary 16 kHz mono WAV, runs local `whisper.cpp` with English forced, then moves the resulting SRT beside the video. Video files are never modified.

The local worker is capped at roughly 75% of the PC's logical CPU threads. During a long film the progress bar is animated while FFmpeg extracts audio and Whisper transcribes; the completed-file count advances after that film has finished. Cancelling does not leave a partial `.srt` next to the video.

## One-time local prerequisites

Put these files somewhere on the Windows PC. After that, the app operates without an internet connection.

- `ffmpeg.exe`
- `whisper-cli.exe` from [whisper.cpp](https://github.com/ggml-org/whisper.cpp)
- an English whisper.cpp GGML/GGUF model, for example `ggml-tiny.en.bin`

For the fastest generation, start with `tiny.en`. It is the default recommendation in the app, but you choose the local model file explicitly. A larger English model gives better recognition at the cost of more time.

If the PC has a supported GPU, choose a GPU-enabled `whisper-cli.exe` build; the app uses the executable you select and needs no different configuration.

## Build

Requirements: Windows 10/11 and the .NET 10 SDK.

```powershell
dotnet build src/WhisperAutoGenSubs/WhisperAutoGenSubs.csproj
dotnet run --project src/WhisperAutoGenSubs/WhisperAutoGenSubs.csproj
```

To produce a self-contained Windows executable:

```powershell
dotnet publish src/WhisperAutoGenSubs/WhisperAutoGenSubs.csproj -c Release -r win-x64 --self-contained true
```

The published application still uses the local FFmpeg, whisper.cpp, and model paths selected in its UI. This keeps the application itself network-free and makes the model choice explicit.
