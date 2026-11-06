using System.Security.Cryptography;
using System.Text;

namespace Cockpit.Core.Mcp;

public sealed class McpToolNames
{
    public const int MaximumLength = 64;
    private const string GatewayPrefix = "mcp__cockpit__";
    private readonly Lock _sync = new();
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _canonical = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _gateway = new(StringComparer.Ordinal);

    public static string Alias(string serverName, ISet<string> used)
    {
        var folded = new string(serverName.Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
        if (!folded.Any(char.IsAsciiLetterOrDigit))
        {
            folded = $"server_{used.Count + 1}";
        }
        var alias = folded;
        for (var suffix = 2; !used.Add(alias); suffix++)
        {
            alias = $"{folded}_{suffix}";
        }
        return alias;
    }

    public string ForTool(string serverName, string toolName)
    {
        lock (_sync)
        {
            if (!_aliases.TryGetValue(serverName, out var alias))
            {
                alias = Alias(serverName, _usedAliases);
                _aliases.Add(serverName, alias);
            }
            return ToGateway($"mcp__{alias}__{toolName}")[GatewayPrefix.Length..];
        }
    }

    public string ToGateway(string name)
    {
        if (!name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return name;
        }
        lock (_sync)
        {
            if (_gateway.TryGetValue(name, out var existing))
            {
                return existing;
            }
            var full = GatewayPrefix + name[5..];
            var candidate = full;
            for (var attempt = 0; candidate.Length > MaximumLength || (_canonical.TryGetValue(candidate, out var previous) && previous != name); attempt++)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{name}/{attempt}")))[..12].ToLowerInvariant();
                candidate = full[..Math.Min(full.Length, MaximumLength - hash.Length - 1)] + "_" + hash;
            }
            _gateway.Add(name, candidate);
            _canonical.Add(candidate, name);
            return candidate;
        }
    }

    public IReadOnlyList<string> TranslateRule(string rule)
    {
        if (!rule.StartsWith("mcp__", StringComparison.Ordinal) || rule.StartsWith(GatewayPrefix, StringComparison.Ordinal))
        {
            return [rule];
        }
        lock (_sync)
        {
            if (!rule.Contains('*') && rule[5..].Contains("__", StringComparison.Ordinal))
            {
                return [ToGateway(rule)];
            }
            var pattern = rule.Contains('*') ? rule : rule + "__*";
            var expression = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            var known = _gateway.Where(pair => System.Text.RegularExpressions.Regex.IsMatch(pair.Key, expression)).Select(pair => pair.Value);
            return [GatewayPrefix + pattern[5..], .. known];
        }
    }

    public string InBrief(string text) => System.Text.RegularExpressions.Regex.Replace(text,
        @"mcp__[A-Za-z0-9_-]+", match => match.Value.StartsWith(GatewayPrefix, StringComparison.Ordinal) ? match.Value : ToGateway(match.Value));

    public string ToCanonical(string name)
    {
        lock (_sync)
        {
            return _canonical.TryGetValue(name, out var canonical) ? canonical
                : name.StartsWith(GatewayPrefix, StringComparison.Ordinal) ? "mcp__" + name[GatewayPrefix.Length..] : name;
        }
    }
}
