namespace DglabFocus.Core;

public sealed record FocusPlan
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "新方案";
    public SessionOptions Options { get; init; } = new();
    public List<AllowRule> Rules { get; init; } = [];

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 40)
            throw new ArgumentException("方案名称需为 1–40 个字。");
        if (Options == null || Rules == null || Rules.Any(x => x == null))
            throw new ArgumentException("方案配置不完整。");
        Options.Validate();
    }

    public FocusPlan Duplicate(string name) => this with { Id = Guid.NewGuid(), Name = name, Rules = [..Rules] };
    public override string ToString() => Name;
}
