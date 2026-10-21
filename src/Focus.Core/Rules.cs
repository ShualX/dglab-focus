using System.Globalization;

namespace DglabFocus.Core;

public enum RuleKind { Application, Domain, Page, PathPrefix }
public enum Attention { Allowed, Outside, Unknown, Neutral }
public sealed record AllowRule(RuleKind Kind, string Value, bool IncludeSubdomains = false)
{
    public string Label => Kind switch
    {
        RuleKind.Application => "软件",
        RuleKind.Domain => IncludeSubdomains ? "域名及子域名" : "域名",
        RuleKind.Page => "具体页面",
        _ => "页面路径"
    };
    public string Display => Kind == RuleKind.Application ? Path.GetFileName(Value) : Value;
}

public static class Rules
{
    public static bool TryHost(string value, out string host)
    {
        host = "";
        value = value.Trim();
        if (string.IsNullOrEmpty(value) || value.Contains('*')) return false;
        if (!Uri.TryCreate(value.Contains("://") ? value : "https://" + value, UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.HostNameType != UriHostNameType.Dns) return false;
        try { host = new IdnMapping().GetAscii(uri.DnsSafeHost.TrimEnd('.')).ToLowerInvariant(); }
        catch (ArgumentException) { return false; }
        return host.Contains('.') && !host.Contains(' ');
    }

    public static bool TryCreate(RuleKind kind, string value, bool subdomains, out AllowRule? rule)
    {
        rule = null;
        value = value.Trim();
        if (kind == RuleKind.Application)
        {
            if (!Path.IsPathFullyQualified(value) || !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            rule = new(kind, Path.GetFullPath(value));
        }
        else if (kind == RuleKind.Domain)
        {
            if (!TryHost(value, out var host)) return false;
            rule = new(kind, host, subdomains);
        }
        else
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !TryHost(value, out _)) return false;
            // Query and fragment can identify a lesson in an SPA. Preserve them for exact-page rules.
            rule = new(kind, uri.AbsoluteUri, false);
        }
        return true;
    }

    public static bool MatchesUrl(AllowRule rule, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !TryHost(url, out var host)) return false;
        if (rule.Kind == RuleKind.Domain)
            return host == rule.Value || (rule.IncludeSubdomains && host.EndsWith("." + rule.Value, StringComparison.Ordinal));
        if (rule.Kind == RuleKind.Page) return uri.AbsoluteUri == rule.Value;
        if (rule.Kind != RuleKind.PathPrefix || !Uri.TryCreate(rule.Value, UriKind.Absolute, out var prefix)) return false;
        if (uri.Scheme != prefix.Scheme || uri.IdnHost != prefix.IdnHost || uri.Port != prefix.Port) return false;
        var path = prefix.AbsolutePath.TrimEnd('/');
        return uri.AbsolutePath == path || uri.AbsolutePath.StartsWith(path + "/", StringComparison.Ordinal);
    }

    public static Attention Evaluate(IReadOnlyCollection<AllowRule> rules, string? executable, string? browserUrl,
        bool isBrowser, bool browserFresh)
    {
        if (string.IsNullOrEmpty(executable)) return Attention.Unknown;
        // Web rules always take precedence over a broad browser executable allowance.
        if (isBrowser && rules.Any(x => x.Kind != RuleKind.Application))
        {
            if (!browserFresh || string.IsNullOrEmpty(browserUrl)) return Attention.Unknown;
            if (!Uri.TryCreate(browserUrl, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return Attention.Unknown;
            return rules.Any(x => MatchesUrl(x, browserUrl)) ? Attention.Allowed : Attention.Outside;
        }
        if (rules.Any(x => x.Kind == RuleKind.Application && string.Equals(x.Value, executable, StringComparison.OrdinalIgnoreCase)))
            return Attention.Allowed;
        if (isBrowser && !browserFresh) return Attention.Unknown;
        return Attention.Outside;
    }
}
