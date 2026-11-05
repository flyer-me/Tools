using System.Text.Json.Serialization;

namespace DiskWriteTracker.Cli;

/// <summary>
/// CLI JSON 输出的源生成上下文。
/// ILLink 裁剪会删除仅被反射读取的属性 getter，反射式 STJ 因此输出空对象；
/// 源生成代码静态引用 getter，裁剪安全，同时消除 IL2026 警告。
/// </summary>
[JsonSerializable(typeof(JsonErrorEnvelope))]
[JsonSerializable(typeof(JsonEnvelope<DoctorData>))]
[JsonSerializable(typeof(JsonEnvelope<DisksData>))]
[JsonSerializable(typeof(JsonEnvelope<ProbeData>))]
[JsonSerializable(typeof(JsonEnvelope<SampleData>))]
[JsonSerializable(typeof(JsonEnvelope<DailyEmptyData>))]
[JsonSerializable(typeof(JsonEnvelope<DailyData>))]
[JsonSerializable(typeof(JsonEnvelope<ReportOutcomeData>))]
[JsonSerializable(typeof(JsonEnvelope<ExportOutcomeData>))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
