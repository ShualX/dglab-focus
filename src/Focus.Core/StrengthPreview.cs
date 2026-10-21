namespace DglabFocus.Core;

// Display-only simulation values. They are never converted to device commands or electrical units.
public sealed record StrengthPreviewOptions
{
    public bool Enabled { get; init; }
    public double Initial { get; init; }
    public double Maximum { get; init; }
    public double Increment { get; init; } = 1;
    public double IntervalSeconds { get; init; } = 30;
    public void Validate()
    {
        if (!double.IsFinite(Initial) || !double.IsFinite(Maximum) || !double.IsFinite(Increment) || !double.IsFinite(IntervalSeconds)
            || Initial < 0 || Maximum > 100 || Maximum < Initial || Increment <= 0 || Increment > 100
            || IntervalSeconds < 1 || IntervalSeconds > 3600)
            throw new ArgumentException("模拟强度需满足 0 ≤ 起始值 ≤ 上限 ≤ 100，递增量为 0–100 之间的正数，间隔为 1–3600 秒。");
    }
    public double At(double cumulativeSeconds, double thresholdSeconds)
    {
        if (!Enabled || !double.IsFinite(cumulativeSeconds) || !double.IsFinite(thresholdSeconds)
            || thresholdSeconds < 0 || cumulativeSeconds < thresholdSeconds) return 0;
        var steps = Math.Floor((cumulativeSeconds - thresholdSeconds) / IntervalSeconds);
        return Math.Min(Maximum, Initial + steps * Increment);
    }
}
