using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using DiskWriteTracker.Core.Analysis;

namespace DiskWriteTracker.Core.Export;

/// <summary>
/// 生成自包含 HTML 报表。
/// C# 只负责骨架、样式、脚本与内嵌数据；卡片、图表、排序筛选全部由内联 JS 在浏览器里完成。
/// 整个文件不含任何 http 字面量，禁止外部引用（无 CDN、无字体、无图片），断网可直接打开。
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
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static string Build(ReportData data)
    {
        var sb = new StringBuilder(48 * 1024);

        sb.Append("<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n");
        sb.Append("<title>硬盘写入量报表</title>\n<style>\n").Append(ReportAssets.Css).Append("\n</style>\n</head>\n<body>\n<div class=\"wrap\">\n");

        AppendHeader(sb, data);
        sb.Append("<div id=\"stale\" class=\"stale\" hidden></div>\n");

        sb.Append("<noscript>\n");
        AppendStaticReport(sb, data);
        sb.Append("</noscript>\n");

        AppendAppShell(sb);

        sb.Append("<script id=\"dwt-data\" type=\"application/json\">");
        sb.Append(SerializeData(data));
        sb.Append("</script>\n");

        sb.Append("<script>\n").Append(ReportAssets.Js).Append("\n</script>\n");
        sb.Append("</div>\n</body>\n</html>\n");

        return sb.ToString();
    }

    /// <summary>兼容旧调用点的重载。内部转成 ReportData，行为与公开入口一致。</summary>
    public static string Build(
        IReadOnlyList<DailyStat> stats,
        IReadOnlyList<DiskDailySummary> summaries,
        string dataDirectory,
        string rangeLabel,
        DateTimeOffset generatedAt)
    {
        var data = ReportData.From(stats, summaries, dataDirectory, rangeLabel, 90, generatedAt);
        return Build(data);
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

    private static void AppendHeader(StringBuilder sb, ReportData data)
    {
        sb.Append("<header class=\"hd\">\n<h1>硬盘写入量报表</h1>\n<p class=\"sub\">");
        sb.Append("区间 ").Append(E(data.Range.Label));
        sb.Append(" ｜ 生成于 ").Append(E(data.GeneratedAt));
        sb.Append(" ｜ 数据截止 ").Append(E(string.IsNullOrEmpty(data.DataQuality.LastDate) ? "—" : data.DataQuality.LastDate));
        sb.Append(" ｜ 数据目录 ").Append(E(data.DataDirectory));
        sb.Append("</p>\n</header>\n");
    }

    private static void AppendAppShell(StringBuilder sb)
    {
        sb.Append("<div id=\"app\" hidden>\n");

        sb.Append("<section class=\"bar\">\n");
        sb.Append("<div class=\"grp\" id=\"diskFilter\"><span class=\"lbl\">磁盘</span></div>\n");
        sb.Append("<div class=\"grp\"><span class=\"lbl\">日期</span>");
        sb.Append("<input type=\"date\" id=\"from\"><span class=\"dash\">至</span><input type=\"date\" id=\"to\"></div>\n");
        sb.Append("<div class=\"grp\" id=\"qualityFilter\"><span class=\"lbl\">质量</span></div>\n");
        sb.Append("<div class=\"grp right\">");
        sb.Append("<button id=\"btnCsv\" class=\"btn\" type=\"button\">导出当前视图 CSV</button>");
        sb.Append("<button id=\"btnReset\" class=\"btn ghost\" type=\"button\">重置</button>");
        sb.Append("</div>\n</section>\n");

        sb.Append("<section class=\"cards\" id=\"cards\"></section>\n");
        sb.Append("<section class=\"diskstrip\" id=\"diskstrip\"></section>\n");

        sb.Append("<section>\n<div class=\"sechead\"><h2>每日写入增量</h2>");
        sb.Append("<div class=\"seg\" id=\"modeSeg\">");
        sb.Append("<button type=\"button\" data-mode=\"bar\" class=\"on\">柱状</button>");
        sb.Append("<button type=\"button\" data-mode=\"line\">折线</button>");
        sb.Append("</div></div>\n");
        sb.Append("<figure class=\"chart\"><div id=\"chart\"></div>");
        sb.Append("<div id=\"tip\" class=\"tip\" hidden></div>");
        sb.Append("<div id=\"legend\" class=\"legend\"></div>");
        sb.Append("<figcaption id=\"cap\"></figcaption></figure>\n</section>\n");

        sb.Append("<section>\n<div class=\"sechead\"><h2>每日明细</h2><span class=\"hint\" id=\"rowHint\"></span></div>\n");
        sb.Append("<div class=\"tablewrap\"><table class=\"tbl\" id=\"tbl\"><thead><tr>");
        AppendHead(sb, "d", "日期", false);
        AppendHead(sb, "model", "磁盘", false);
        AppendHead(sb, "x", "日增量", true);
        AppendHead(sb, "s", "起始累计", true);
        AppendHead(sb, "e", "结束累计", true);
        AppendHead(sb, "m", "方法", false);
        AppendHead(sb, "q", "数据质量", false);
        AppendHead(sb, "note", "备注", false);
        sb.Append("</tr></thead><tbody></tbody></table></div>\n");
        sb.Append("<div class=\"pager\" id=\"pager\" hidden></div>\n</section>\n");

        sb.Append("<section id=\"dq\"></section>\n");
        sb.Append("<footer>DiskWriteTracker ｜ 数据来自硬盘固件计数器，本地生成，不上传任何信息</footer>\n");
        sb.Append("</div>\n");
    }

    private static void AppendHead(StringBuilder sb, string col, string label, bool numeric)
    {
        sb.Append("<th data-col=\"").Append(col).Append('"');
        if (numeric) sb.Append(" class=\"num\"");
        sb.Append('>').Append(E(label)).Append("<span class=\"rank\"></span><span class=\"ar\">↕</span></th>");
    }

    /// <summary>禁用 JS 时的降级视图：同样有汇总卡片和完整明细表，不做筛选与排序。</summary>
    private static void AppendStaticReport(StringBuilder sb, ReportData data)
    {
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
            return;
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

    /// <summary>与 CsvExporter.FormatBytes 保持一致：1000 进制、最多 3 位小数、无千分位。</summary>
    private static string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue) return "—";
        double value = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        int unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return value.ToString("0.###", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string E(string? value) => string.IsNullOrEmpty(value)
        ? string.Empty
        : value.Replace("&", "&amp;", StringComparison.Ordinal)
               .Replace("<", "&lt;", StringComparison.Ordinal)
               .Replace(">", "&gt;", StringComparison.Ordinal)
               .Replace("\"", "&quot;", StringComparison.Ordinal)
               .Replace("'", "&#39;", StringComparison.Ordinal);
}
