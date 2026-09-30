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
    double? TotalGpuPercent);
