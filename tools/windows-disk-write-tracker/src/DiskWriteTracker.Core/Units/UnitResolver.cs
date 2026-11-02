namespace DiskWriteTracker.Core.Units;

/// <summary>单位裁决结论。</summary>
public sealed record UnitDecision(
    long Scale,
    string Quality,
    double? ImpliedTbwTb,
    string Reason);

/// <summary>
/// 用"磨损一致性闸门"判定 SATA 累计写入的单位，避免把 512B 扇区数直接当字节用。
/// 原理：累计字节 ÷ 已用寿命百分比 = 隐含标称 TBW，该值必须落在合理区间内。
/// 错误单位会推出荒谬的 TBW（差 512 倍或 8 倍），据此排除。
/// </summary>
public static class UnitResolver
{
    private const double BandLowRatio = 0.4;
    private const double BandHighRatio = 6.0;
    private const double FallbackLowTb = 100;
    private const double FallbackHighTb = 2000;

    /// <summary>
    /// 已知扇区大小时优先用它；磨损闸门仅用于交叉验证与排除。
    /// percentUsed 为固件报告的"已用百分比"（0 表示新盘，无法闸门）。
    /// </summary>
    public static UnitDecision Resolve(long rawLba, int logicalSectorSize, int? percentUsed, double? declaredTbwTb)
    {
        if (rawLba <= 0)
            return new UnitDecision(0, "missing", null, "原始值为 0 或缺失");

        if (percentUsed is null || percentUsed <= 0)
        {
            // 无磨损数据无法闸门：按规范扇区大小换算，但保持保守标记
            return new UnitDecision(
                logicalSectorSize,
                "fallback",
                null,
                $"缺少磨损百分比，无法交叉验证；按逻辑扇区 {logicalSectorSize} 字节换算");
        }

        double usedRatio = percentUsed.Value / 100.0;
        double low = declaredTbwTb is > 0 ? declaredTbwTb.Value * BandLowRatio : FallbackLowTb;
        double high = declaredTbwTb is > 0 ? declaredTbwTb.Value * BandHighRatio : FallbackHighTb;

        var passed = new List<(long Scale, double ImpliedTbw)>();

        // 只考虑物理上可能的两种解释：原始值是逻辑扇区数，或原始值直接是字节。
        // 4096 只在盘本身是 4Kn 时才有意义，此时 logicalSectorSize 已经是 4096。
        foreach (long scale in new[] { logicalSectorSize, 1L }.Distinct())
        {
            double totalTb = rawLba * (double)scale / 1e12;
            double impliedTbw = totalTb / usedRatio;

            if (impliedTbw >= low && impliedTbw <= high)
                passed.Add((scale, impliedTbw));
        }

        if (passed.Count == 1)
        {
            var (scale, implied) = passed[0];
            string source = scale == logicalSectorSize ? "与逻辑扇区大小一致" : "与逻辑扇区大小不同，以闸门为准";
            return new UnitDecision(
                scale,
                "reliable",
                implied,
                $"磨损闸门唯一通过：隐含 TBW {implied:0.#} TB 落在 [{low:0.#}, {high:0.#}] TB，{source}");
        }

        if (passed.Count > 1)
        {
            long preferred = passed.Any(p => p.Scale == logicalSectorSize) ? logicalSectorSize : passed[0].Scale;
            return new UnitDecision(
                preferred,
                "low_confidence",
                passed.First(p => p.Scale == preferred).ImpliedTbw,
                $"多个单位同时通过闸门（{string.Join(", ", passed.Select(p => p.Scale))}），无法唯一确定");
        }

        // 无候选通过：不输出字节值，避免静默猜测
        return new UnitDecision(
            0,
            "low_confidence",
            0,
            $"所有候选单位均未通过磨损闸门（期望隐含 TBW ∈ [{low:0.#}, {high:0.#}] TB，已用 {percentUsed}%）");
    }
}
