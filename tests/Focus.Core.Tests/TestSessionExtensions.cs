using DglabFocus.Core;

// Keep the existing timing tests readable while all production starts require a plan.
internal static class TestSessionExtensions
{
    public static void Start(this Session session, SessionOptions options, IEnumerable<AllowRule> rules, DateTimeOffset now)
        => session.Start(new FocusPlan { Name = "测试方案", Options = options, Rules = [..rules] }, now);
}
