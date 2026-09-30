namespace WhisperAutoGenSubs.Services;

public sealed record GenerationProgress(
    string Phase,
    double? Percent = null,
    TimeSpan? EstimatedRemaining = null,
    double? RealtimeFactor = null,
    string? Engine = null,
    ProcessTelemetry? Telemetry = null);

public sealed record ProcessTelemetry(
    double ProcessCpuPercent,
    double SystemCpuPercent,
    double? ProcessGpuPercent,
    double? TotalGpuPercent,
    NvidiaTelemetry? Nvidia = null);

/// <summary>
/// A sample reported by NVIDIA's installed driver through nvidia-smi.
/// GPU utilization is for the whole named card; process memory, when present,
/// is matched to the exact whisper-cli process ID.
/// </summary>
public sealed record NvidiaTelemetry(
    string GpuName,
    double? GpuUtilizationPercent,
    int? MemoryUsedMiB,
    int? TemperatureC,
    double? PowerWatts,
    int? ProcessMemoryUsedMiB);
