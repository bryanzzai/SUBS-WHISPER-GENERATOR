# Whisper Auto Gen Subs

Windows desktop app that recursively scans a local video library and generates an English `.srt` sidecar next to the videos selected in the result list.

It is deliberately offline at runtime. It does not call OpenSubtitles, any subtitle site, OpenAI, or any other web service.

## What it does

```text
Silo S03E01.mkv  ->  Silo S03E01.srt
```

1. Choose the root of a video library.
2. Choose local copies of `ffmpeg.exe`, `whisper-cli.exe`, and an English whisper.cpp model.
3. Scan.
4. Select the videos to process in the first column, then click **Generate selected subtitles**. An existing `.srt` beside a selected video is replaced.

The app extracts the first audio track into a temporary 16 kHz mono WAV, runs local `whisper.cpp` with English forced, then moves the resulting SRT beside the video. Video files are never modified.

## Live progress and instrument panel

The application no longer substitutes an animated bar for progress. While a file is running, its dashboard shows:

- the exact file and phase;
- real percentage from FFmpeg while audio is extracted, and from whisper.cpp while speech is transcribed;
- estimated time remaining and processing speed in multiples of real time;
- CPU use for the active worker process and the whole PC;
- GPU use for the active worker process and the busiest GPU engine on the PC.

The GPU readings come from Windows' GPU Engine performance counters. If a driver does not expose those counters, the application says so explicitly rather than showing a made-up number. `ffprobe.exe`, normally supplied beside `ffmpeg.exe`, gives the video duration needed for the precise extraction percentage. Without it, subtitle generation still works but the extraction phase is marked as duration unavailable.

Cancelling does not leave a partial `.srt` next to the video.

## One-time local prerequisites

Put these files somewhere on the Windows PC. After that, the app operates without an internet connection.

- `ffmpeg.exe`
- `whisper-cli.exe` from [whisper.cpp](https://github.com/ggml-org/whisper.cpp)
- an English whisper.cpp GGML/GGUF model, for example `ggml-tiny.en.bin`

For the fastest generation, start with `tiny.en`. It is the default recommendation in the app, but you choose the local model file explicitly. A larger English model gives better recognition at the cost of more time.

For the user's NVIDIA GeForce GTX 1650, select a CUDA-enabled `whisper-cli.exe` build. The ordinary `whisper-bin-x64.zip` is not automatically GPU-enabled: use a CUDA build that matches the installed NVIDIA driver/runtime and keep its DLL files beside `whisper-cli.exe`. The dashboard will confirm `CUDA GPU active` only after the running executable reports a CUDA backend; otherwise it clearly reports `CPU-only executable`.

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
