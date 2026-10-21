using System.Globalization;

namespace DglabFocus.Core;

public enum RuleKind { Application, Domain, Page, PathPrefix }
public enum RuleAction { Allow, Block }
public enum RuleMode { Whitelist, Blacklist }
public enum Attention { Allowed, Outside, Unknown, Neutral }
public sealed record AllowRule(RuleKind Kind, string Value, bool IncludeSubdomains = false, RuleAction Action = RuleAction.Allow)
{
    public string KindLabel => Kind switch
    {
        RuleKind.Application => "软件",
        RuleKind.Domain => IncludeSubdomains ? "域名及子域名" : "域名",
        RuleKind.Page => "具体页面",
        _ => "页面路径"
    };
    public string Label => (Action == RuleAction.Block ? "黑名单 · " : "白名单 · ") + KindLabel;
    public bool IsBlocked => Action == RuleAction.Block;
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
        bool isBrowser, bool browserFresh, RuleMode applicationMode = RuleMode.Whitelist, RuleMode websiteMode = RuleMode.Whitelist)
    {
        if (string.IsNullOrEmpty(executable)) return Attention.Unknown;
        bool AppMatch(AllowRule rule) => rule.Kind == RuleKind.Application
            && string.Equals(rule.Value, executable, StringComparison.OrdinalIgnoreCase);
        if (rules.Any(x => x.Action == RuleAction.Block && AppMatch(x))) return Attention.Outside;
        var appAllowed = rules.Any(x => x.Action == RuleAction.Allow && AppMatch(x));
        if (isBrowser)
        {
            var webRules = rules.Where(x => x.Kind != RuleKind.Application).ToList();
            // Preserve old software-only browser allowances, but never let them bypass a web policy.
            if (webRules.Count == 0 && websiteMode == RuleMode.Whitelist && appAllowed) return Attention.Allowed;
            if (!browserFresh || string.IsNullOrEmpty(browserUrl)) return Attention.Unknown;
            if (!Uri.TryCreate(browserUrl, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return Attention.Unknown;
            if (webRules.Any(x => x.Action == RuleAction.Block && MatchesUrl(x, browserUrl))) return Attention.Outside;
            if (webRules.Any(x => x.Action == RuleAction.Allow && MatchesUrl(x, browserUrl))) return Attention.Allowed;
            return websiteMode == RuleMode.Blacklist ? Attention.Allowed : Attention.Outside;
        }
        return appAllowed || applicationMode == RuleMode.Blacklist ? Attention.Allowed : Attention.Outside;
    }
}
