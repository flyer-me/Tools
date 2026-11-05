using System.Text.Json.Serialization;

namespace DiskWriteTracker.Cli;

/// <summary>
/// CLI 的 JSON 输出载荷。匿名类型在 ILLink 裁剪下构造参数名会被删除，
/// System.Text.Json 反射序列化因此失败（"parameters with null names"），
/// 故统一改为命名类型：无参构造 + 可写属性 + 显式 JsonPropertyName，
/// JSON 字段名与历史匿名版本逐字一致（verify.ps1 按这些名字断言）。
/// </summary>

internal sealed class JsonErrorEnvelope
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

internal sealed class JsonEnvelope<TData>
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("data")] public TData? Data { get; set; }
}

// ---------- doctor ----------

internal sealed class DoctorConfigData
{
    [JsonPropertyName("sourcePath")] public string? SourcePath { get; set; }
    [JsonPropertyName("loadWarning")] public string? LoadWarning { get; set; }
    [JsonPropertyName("retentionDays")] public int RetentionDays { get; set; }
    [JsonPropertyName("timeoutSeconds")] public int TimeoutSeconds { get; set; }
    [JsonPropertyName("dailyTime")] public string? DailyTime { get; set; }
    [JsonPropertyName("requireWearGate")] public bool RequireWearGate { get; set; }
    [JsonPropertyName("logLevel")] public string? LogLevel { get; set; }
}

internal sealed class DoctorData
{
    [JsonPropertyName("os")] public string Os { get; set; } = string.Empty;
    [JsonPropertyName("osBuild")] public int OsBuild { get; set; }
    [JsonPropertyName("runtime")] public string Runtime { get; set; } = string.Empty;
    [JsonPropertyName("isAdmin")] public bool IsAdmin { get; set; }
    [JsonPropertyName("enumeratedDrives")] public int EnumeratedDrives { get; set; }
    [JsonPropertyName("dataDirectory")] public string DataDirectory { get; set; } = string.Empty;
    [JsonPropertyName("dataDirectoryIsFallback")] public bool DataDirectoryIsFallback { get; set; }
    [JsonPropertyName("dataDirectoryIsPortable")] public bool DataDirectoryIsPortable { get; set; }
    [JsonPropertyName("dataDirectoryWarning")] public string? DataDirectoryWarning { get; set; }
    [JsonPropertyName("migrationNote")] public string? MigrationNote { get; set; }
    [JsonPropertyName("sampleCount")] public int SampleCount { get; set; }
    [JsonPropertyName("settingsPath")] public string SettingsPath { get; set; } = string.Empty;
    [JsonPropertyName("logDirectory")] public string LogDirectory { get; set; } = string.Empty;
    [JsonPropertyName("config")] public DoctorConfigData Config { get; set; } = new();
}

// ---------- disks ----------

internal sealed class DiskItemData
{
    [JsonPropertyName("driveNumber")] public int DriveNumber { get; set; }
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("serialNumber")] public string SerialNumber { get; set; } = string.Empty;
    [JsonPropertyName("serialIsSynthetic")] public bool SerialIsSynthetic { get; set; }
    [JsonPropertyName("diskKey")] public string DiskKey { get; set; } = string.Empty;
    [JsonPropertyName("firmware")] public string Firmware { get; set; } = string.Empty;
    [JsonPropertyName("busType")] public string BusType { get; set; } = string.Empty;
    [JsonPropertyName("sizeBytes")] public long? SizeBytes { get; set; }
    [JsonPropertyName("accessLevel")] public string AccessLevel { get; set; } = string.Empty;
}

internal sealed class DisksData
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("disks")] public List<DiskItemData> Disks { get; set; } = new();
}

// ---------- probe ----------

internal sealed class ProbeItemData
{
    [JsonPropertyName("driveNumber")] public int DriveNumber { get; set; }
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("serialNumber")] public string SerialNumber { get; set; } = string.Empty;
    [JsonPropertyName("busType")] public string BusType { get; set; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
    [JsonPropertyName("attributeRef")] public string? AttributeRef { get; set; }
    [JsonPropertyName("rawValue")] public long? RawValue { get; set; }
    [JsonPropertyName("rawUnit")] public string? RawUnit { get; set; }
    [JsonPropertyName("unitScale")] public long UnitScale { get; set; }
    [JsonPropertyName("totalBytes")] public long? TotalBytes { get; set; }
    [JsonPropertyName("totalTB")] public double? TotalTb { get; set; }
    [JsonPropertyName("quality")] public string Quality { get; set; } = "missing";
    [JsonPropertyName("impliedTbwTb")] public double? ImpliedTbwTb { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("temperatureC")] public double? TemperatureC { get; set; }
    [JsonPropertyName("powerOnHours")] public long? PowerOnHours { get; set; }
    [JsonPropertyName("percentageUsed")] public int? PercentageUsed { get; set; }
}

internal sealed class ProbeData
{
    [JsonPropertyName("capturedUtc")] public string CapturedUtc { get; set; } = string.Empty;
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("disks")] public List<ProbeItemData> Disks { get; set; } = new();
}

// ---------- sample ----------

internal sealed class SampleItemData
{
    [JsonPropertyName("diskKey")] public string DiskKey { get; set; } = string.Empty;
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("totalBytes")] public long? TotalBytes { get; set; }
    [JsonPropertyName("totalTB")] public double? TotalTb { get; set; }
    [JsonPropertyName("quality")] public string Quality { get; set; } = "missing";
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
    [JsonPropertyName("capturedLocal")] public string CapturedLocal { get; set; } = string.Empty;
}

internal sealed class SampleData
{
    [JsonPropertyName("runId")] public string RunId { get; set; } = string.Empty;
    [JsonPropertyName("dataDirectory")] public string DataDirectory { get; set; } = string.Empty;
    [JsonPropertyName("dataDirectoryIsFallback")] public bool DataDirectoryIsFallback { get; set; }
    [JsonPropertyName("dataDirectoryIsPortable")] public bool DataDirectoryIsPortable { get; set; }
    [JsonPropertyName("migrationNote")] public string? MigrationNote { get; set; }
    [JsonPropertyName("file")] public string? File { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("disks")] public List<SampleItemData> Disks { get; set; } = new();
}

// ---------- daily ----------

internal sealed class DailyStatItemData
{
    [JsonPropertyName("LocalDate")] public string LocalDate { get; set; } = string.Empty;
    [JsonPropertyName("Model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("dailyBytes")] public long DailyBytes { get; set; }
    [JsonPropertyName("dailyHuman")] public string DailyHuman { get; set; } = string.Empty;
    [JsonPropertyName("startBytes")] public long? StartBytes { get; set; }
    [JsonPropertyName("endBytes")] public long? EndBytes { get; set; }
    [JsonPropertyName("Method")] public string Method { get; set; } = string.Empty;
    [JsonPropertyName("Quality")] public string Quality { get; set; } = string.Empty;
    [JsonPropertyName("GapDays")] public int GapDays { get; set; }
    [JsonPropertyName("SampleCount")] public int SampleCount { get; set; }
    [JsonPropertyName("Note")] public string? Note { get; set; }
}

internal sealed class DailyEmptyData
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}

internal sealed class DailyData
{
    [JsonPropertyName("days")] public int Days { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("stats")] public List<DailyStatItemData> Stats { get; set; } = new();
}

// ---------- report / export ----------

internal sealed class ReportOutcomeData
{
    [JsonPropertyName("file")] public string? File { get; set; }
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("rows")] public int Rows { get; set; }
    [JsonPropertyName("disks")] public int Disks { get; set; }
}

internal sealed class ExportOutcomeData
{
    [JsonPropertyName("daily")] public string DailyFile { get; set; } = string.Empty;
    [JsonPropertyName("samples")] public string SamplesFile { get; set; } = string.Empty;
    [JsonPropertyName("dailyRows")] public int DailyRows { get; set; }
    [JsonPropertyName("sampleRows")] public int SampleRows { get; set; }
}
