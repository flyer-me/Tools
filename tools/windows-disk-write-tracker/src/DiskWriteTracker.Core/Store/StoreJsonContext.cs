using System.Text.Json.Serialization;

namespace DiskWriteTracker.Core.Store;

/// <summary>JSONL 序列化上下文。camelCase 字段名与既有数据文件保持一致。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SampleRecord))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;
