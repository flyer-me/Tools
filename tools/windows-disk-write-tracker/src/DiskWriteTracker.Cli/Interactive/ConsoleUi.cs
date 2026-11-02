namespace DiskWriteTracker.Cli.Interactive;

/// <summary>
/// 控制台渲染原语。所有方法在输出被重定向时退化为纯文本，不会把清屏转义写进文件。
/// </summary>
internal static class ConsoleUi
{
    /// <summary>由菜单入口设置。false 时 Clear 不生效、不画颜色。</summary>
    public static bool Interactive { get; set; }

    public static void Clear()
    {
        if (!Interactive)
            return;

        try
        {
            Console.Clear();
        }
        catch (IOException)
        {
            // 某些宿主没有真正的控制台缓冲区
        }
    }

    public static void Line(string text = "") => Console.WriteLine(text);

    public static void Info(string text) => Write(text, null);

    public static void Ok(string text) => Write(text, ConsoleColor.Green);

    public static void Warn(string text) => Write(text, ConsoleColor.Yellow);

    public static void Fail(string text) => Write(text, ConsoleColor.Red);

    public static void Faint(string text) => Write(text, ConsoleColor.DarkGray);

    public static void Accent(string text) => Write(text, ConsoleColor.Cyan);

    private static void Write(string text, ConsoleColor? color)
    {
        if (color is null || !Interactive)
        {
            Console.WriteLine(text);
            return;
        }

        ConsoleColor original = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color.Value;
            Console.WriteLine(text);
        }
        finally
        {
            Console.ForegroundColor = original;
        }
    }

    /// <summary>读一行输入。输入流已结束（EOF）时返回 null，调用方据此退出。</summary>
    public static string? Prompt(string label)
    {
        Console.Write(label);
        return Console.ReadLine();
    }

    public static void Pause()
    {
        if (!Interactive)
            return;

        Console.WriteLine();
        Faint("  按回车返回菜单...");
        Console.ReadLine();
    }

    /// <summary>按显示宽度右补空格。中日韩字符计 2 列，否则表格会歪。</summary>
    public static string Pad(string text, int width)
    {
        int w = DisplayWidth(text);
        return w >= width ? text : text + new string(' ', width - w);
    }

    /// <summary>按显示宽度截断并加省略号。</summary>
    public static string Fit(string text, int width)
    {
        if (DisplayWidth(text) <= width)
            return text;

        var sb = new System.Text.StringBuilder();
        int used = 0;

        foreach (char c in text)
        {
            int w = IsWide(c) ? 2 : 1;
            if (used + w > width - 1)
                break;

            sb.Append(c);
            used += w;
        }

        return sb.Append('…').ToString();
    }

    public static int DisplayWidth(string text)
    {
        int width = 0;
        foreach (char c in text)
            width += IsWide(c) ? 2 : 1;
        return width;
    }

    private static bool IsWide(char c) =>
        (c >= 0x1100 && c <= 0x115F)
        || (c >= 0x2E80 && c <= 0xA4CF)
        || (c >= 0xAC00 && c <= 0xD7A3)
        || (c >= 0xF900 && c <= 0xFAFF)
        || (c >= 0xFE30 && c <= 0xFE6F)
        || (c >= 0xFF00 && c <= 0xFF60)
        || (c >= 0xFFE0 && c <= 0xFFE6);
}
