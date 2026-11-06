using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace DiskWriteTracker.Core.Export;

/// <summary>
/// 自包含 HTML 报表。骨架在 report-template.html，这里只负责填动态值：
/// 头部元信息、noscript 降级视图、内嵌 JSON。卡片、图表、筛选排序由内联 JS
/// 在浏览器里完成。整个文件不含 http 字面量，断网可直接打开。
/// </summary>
public static class HtmlReportBuilder
{
    /// <summary>
    /// 内嵌 JSON 的序列化选项。中文保留可读（UnicodeRanges.All），但所有 '&lt;' 在写入前被转成
    /// \u003C —— 型号或备注里出现 &lt;/script&gt; 也无法截断脚本块。
    /// </summary>
    private static readonly JsonSerializerOptions DataJson = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        TypeInfoResolver = ReportJsonContext.Default
    };

    public static string Build(ReportData data)
    {
        // DATA 最后填：它承载任意用户字符串，先填其他占位符可避免其内容被再次扫描
        return ReportAssets.Template
            .Replace("{{CSS}}", ReportAssets.Css, StringComparison.Ordinal)
            .Replace("{{JS}}", ReportAssets.Js, StringComparison.Ordinal)
            .Replace("{{STATIC_REPORT}}", BuildStaticReport(data), StringComparison.Ordinal)
            .Replace("{{RANGE_LABEL}}", E(data.Range.Label))
            .Replace("{{GENERATED_AT}}", E(data.GeneratedAt))
            .Replace("{{LAST_DATE}}", E(string.IsNullOrEmpty(data.DataQuality.LastDate) ? "—" : data.DataQuality.LastDate))
            .Replace("{{DATA_DIRECTORY}}", E(data.DataDirectory))
            .Replace("{{DATA}}", SerializeData(data), StringComparison.Ordinal);
    }

    /// <summary>质量标记的中文标签，供 CSV 导出与命令行共用。</summary>
    public static string QualityLabel(string quality) => quality switch
    {
        "reliable" => "可靠",
        "fallback" => "回退",
        "low_confidence" => "低可信",
        "anomaly" => "异常",
        "missing" => "缺失",
        _ => quality
    };

    private static string SerializeData(ReportData data)
    {
        string json = JsonSerializer.Serialize(data, DataJson);

        // '<' 在 JSON 里只可能出现在字符串内部，替换成转义序列既合法又彻底断掉 </script> 与 <!--
        return json.Replace("<", "\\u003C", StringComparison.Ordinal);
    }

    /// <summary>禁用 JS 时的降级视图：汇总卡片和完整明细表，不做筛选与排序。</summary>
    private static string BuildStaticReport(ReportData data)
    {
        var sb = new StringBuilder(16 * 1024);
        sb.Append("<section class=\"cards\">\n");

        var cards = new (string Label, string Value, string Note)[]
        {
            ("今日总写入", FormatBytes(data.Cards.TodayBytes), data.Today),
            ("昨日总写入", FormatBytes(data.Cards.YesterdayBytes), data.Yesterday),
            ("近 7 日均值", FormatBytes(data.Cards.Last7Bytes / 7), "合计 " + FormatBytes(data.Cards.Last7Bytes)),
            ("累计写入", FormatBytes(data.Cards.CumulativeBytes), "全生命周期"),
            ("异常磁盘", data.Cards.AnomalyDisks + " / " + data.Disks.Count,
                data.Cards.AnomalyDisks > 0 ? "存在回退或缺数" : "未发现异常")
        };

        foreach (var (label, value, note) in cards)
        {
            sb.Append("<article class=\"card\"><div class=\"k\">").Append(E(label)).Append("</div>");
            sb.Append("<div class=\"v\">").Append(E(value)).Append("</div>");
            sb.Append("<div class=\"n\">").Append(E(note)).Append("</div></article>\n");
        }
        sb.Append("</section>\n");

        if (data.Rows.Count == 0)
        {
            sb.Append("<div class=\"emptybox\">所选区间内没有可用的日统计记录。请先运行采样，或放宽日期范围。</div>\n");
            sb.Append("<p class=\"qnote\">当前浏览器禁用了 JavaScript，这里显示的是静态视图，无法使用筛选与排序。</p>\n");
            return sb.ToString();
        }

        var diskName = data.Disks.ToDictionary(d => d.Key, d => d.Model, StringComparer.Ordinal);

        sb.Append("<section>\n<div class=\"sechead\"><h2>每日明细（静态视图）</h2></div>\n");
        sb.Append("<div class=\"tablewrap\"><table class=\"tbl\"><thead><tr>");
        foreach (string h in new[] { "日期", "磁盘", "日增量", "起始累计", "结束累计", "方法", "数据质量", "备注" })
            sb.Append("<th>").Append(E(h)).Append("</th>");
        sb.Append("</tr></thead><tbody>\n");

        foreach (var r in data.Rows.OrderByDescending(x => x.D, StringComparer.Ordinal).ThenBy(x => x.K, StringComparer.Ordinal))
        {
            sb.Append("<tr>");
            sb.Append("<td>").Append(E(r.D)).Append("</td>");
            sb.Append("<td>").Append(E(diskName.TryGetValue(r.K, out var model) ? model : r.K)).Append("</td>");
            sb.Append("<td class=\"num\">").Append(E(FormatBytes(r.X))).Append("</td>");
            sb.Append("<td class=\"num\">").Append(E(FormatBytes(r.S))).Append("</td>");
            sb.Append("<td class=\"num\">").Append(E(FormatBytes(r.E))).Append("</td>");
            sb.Append("<td>").Append(E(MethodLabel(r.M))).Append("</td>");
            sb.Append("<td><span class=\"badge q-").Append(E(r.Q)).Append("\">").Append(E(QualityLabel(r.Q))).Append("</span></td>");
            sb.Append("<td class=\"note\">").Append(E(r.Note)).Append("</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody></table></div>\n</section>\n");

        sb.Append("<p class=\"qnote\">当前浏览器禁用了 JavaScript，这里显示的是静态视图，无法使用筛选与排序。</p>\n");
        return sb.ToString();
    }

    private static string MethodLabel(string method) => method switch
    {
        "baseline" => "建立基线",
        "counter_diff" => "相邻日差值",
        "source_switch" => "来源切换",
        "gap_filled" => "跨期累计",
        "clamped" => "回退钳制",
        _ => method
    };

    /// <summary>报表里空值呈现为 "—"。CSV 那边是空串，所以只包装一层，不复制实现。</summary>
    private static string FormatBytes(long? bytes) => CsvExporter.FormatBytes(bytes, "—");

    private static string E(string? value) => string.IsNullOrEmpty(value)
        ? string.Empty
        : value.Replace("&", "&amp;", StringComparison.Ordinal)
               .Replace("<", "&lt;", StringComparison.Ordinal)
               .Replace(">", "&gt;", StringComparison.Ordinal)
               .Replace("\"", "&quot;", StringComparison.Ordinal)
               .Replace("'", "&#39;", StringComparison.Ordinal);
}
