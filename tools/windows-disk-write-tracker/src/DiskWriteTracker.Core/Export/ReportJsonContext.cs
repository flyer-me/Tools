using System.Text.Json.Serialization;

namespace DiskWriteTracker.Core.Export;

/// <summary>
/// 报表内嵌 JSON 的源生成上下文。
/// ILLink 裁剪会删除仅被反射读取的属性 getter（对象初始化器只写不读），
/// 反射式 STJ 序列化因此输出空对象；源生成代码静态引用 getter，裁剪安全。
/// </summary>
[JsonSerializable(typeof(ReportData))]
internal sealed partial class ReportJsonContext : JsonSerializerContext;
