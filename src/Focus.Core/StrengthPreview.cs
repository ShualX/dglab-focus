namespace DglabFocus.Core;

// Normalized curve percentages. Device requests are separately bounded by a manually authorized runtime cap.
public sealed record StrengthPreviewOptions
{
    public bool Enabled { get; init; } = true;
    public double Initial { get; init; } = 10;
    public double Maximum { get; init; } = 40;
    public double Increment { get; init; } = 5;
    public double IntervalSeconds { get; init; } = 30;
    public double IncidentIncrement { get; init; } = 5;
    public void Validate()
    {
        if (!double.IsFinite(Initial) || !double.IsFinite(Maximum) || !double.IsFinite(Increment) || !double.IsFinite(IntervalSeconds)
            || Initial < 0 || Maximum > 100 || Maximum < Initial || Increment <= 0 || Increment > 100
            || IntervalSeconds < 1 || IntervalSeconds > 3600 || !double.IsFinite(IncidentIncrement) || IncidentIncrement < 0 || IncidentIncrement > 100)
            throw new ArgumentException("曲线需满足 0 ≤ 起始值 ≤ 上限 ≤ 100，递增量为 0–100 之间的正数，间隔为 1–3600 秒，重复分心附加值为 0–100。");
    }
    public double At(double cumulativeSeconds, int recentIncidents = 1)
    {
        if (!Enabled || !double.IsFinite(cumulativeSeconds) || cumulativeSeconds < 0) return 0;
        var steps = Math.Floor(cumulativeSeconds / IntervalSeconds);
        return Math.Min(Maximum, Initial + steps * Increment + Math.Max(0, recentIncidents - 1) * IncidentIncrement);
    }
}
