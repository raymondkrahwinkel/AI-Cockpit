using System.Text;

namespace Cockpit.Infrastructure.Clones;

// Parses a git remote URL into the pieces the clone manager needs (AC-90): the URL git is handed, the
// managed slug/folder the clone lives under, and a normalized identity used for de-duplication.
// Security: `user:password@` credentials are stripped from `RemoteUrl` for HTTP(S); an SSH `git@` login user is kept, only its password stripped.
internal sealed class GitCloneUrl
{
    private GitCloneUrl(string remoteUrl, string host, IReadOnlyList<string> segments)
    {
        RemoteUrl = remoteUrl;
        Host = host;
        Segments = segments;
    }

    // The URL git is handed — credentials stripped for HTTPS, so no secret reaches argv or `.git/config`.
    public string RemoteUrl { get; }

    // The host the repository lives on, lowercased (`github.com`), the first slug segment.
    public string Host { get; }

    // The path segments under the host — `[org, repo]`, or more for a nested group — sanitized and lowercased.
    public IReadOnlyList<string> Segments { get; }

    // The stable slug `host/org/repo` used for the managed folder and to name the clone.
    public string Slug => string.Join('/', new[] { Host }.Concat(Segments));

    // The slug as an OS-native relative path (`host/org/repo` under the clones root).
    public string RelativePath => System.IO.Path.Combine(new[] { Host }.Concat(Segments).ToArray());

    // The identity two remotes are the same repository by: host plus path, lowercased, scheme/credentials/port and
    // a trailing `.git` ignored — so `https://github.com/o/r.git` and `git@github.com:o/r` match.
    public string NormalizedKey => Slug;

    // Parses `url`, or throws when it is not a git URL a clone could be built from — an empty
    // string, no host, or no repository name. The message is safe to surface: it never echoes the raw URL, which
    // could carry a token.
    public static GitCloneUrl Parse(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new FormatException("Enter a repository URL to clone.");
        }

        var trimmed = url.Trim();

        return _TryParseScpLike(trimmed, out var scp)
            ? scp
            : _ParseSchemeUrl(trimmed);
    }

    // Whether `otherRemoteUrl` (an existing checkout's `origin`) is the same repository as this one — the de-dup test. A remote that will not parse is treated as "not the same", the safe direction.
    public bool SameRepositoryAs(string otherRemoteUrl)
    {
        try
        {
            return string.Equals(NormalizedKey, Parse(otherRemoteUrl).NormalizedKey, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // scp-style: [user@]host:path — git's shorthand SSH form (git@github.com:org/repo.git). Distinguished from a
    // scheme URL by a colon that is not part of "://" and comes before any slash; a Windows drive path (C:\...) is
    // excluded by requiring a host with a dot or the git@ user, which a drive letter never has.
    private static bool _TryParseScpLike(string url, out GitCloneUrl parsed)
    {
        parsed = null!;

        if (url.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var colon = url.IndexOf(':');
        if (colon <= 0 || url.IndexOf('/') is var slash && slash >= 0 && slash < colon)
        {
            return false;
        }

        var authority = url[..colon];
        var path = url[(colon + 1)..];

        var user = string.Empty;
        var host = authority;
        var at = authority.IndexOf('@');
        if (at >= 0)
        {
            user = authority[..at];
            host = authority[(at + 1)..];
        }

        // Not scp-like unless it looks like a host: an explicit git@ user, or a dotted hostname. Guards a bare
        // "word:something" (and a Windows "C:\path") from being mistaken for an SSH remote.
        if (string.IsNullOrEmpty(host) || (user.Length == 0 && !host.Contains('.')))
        {
            return false;
        }

        var segments = _Segments(path);
        if (segments.Count == 0)
        {
            return false;
        }

        // Kept verbatim (credentials-free by construction — an SSH user is not a secret) so git clones with exactly
        // what the operator gave.
        parsed = new GitCloneUrl(url, host.ToLowerInvariant(), segments);
        return true;
    }

    private static GitCloneUrl _ParseSchemeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new FormatException("That is not a valid repository URL.");
        }

        var scheme = uri.Scheme.ToLowerInvariant();

        // A local file:// URL (git clones these too) has no network host — key it under a synthetic "local" host so
        // it still lands under a stable slug, and hand git the URL verbatim (there is nothing to strip).
        if (scheme is "file")
        {
            var fileSegments = _Segments(uri.LocalPath);
            if (fileSegments.Count == 0)
            {
                throw new FormatException("The repository URL has no repository path.");
            }

            return new GitCloneUrl(url, "local", fileSegments);
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            throw new FormatException("The repository URL has no host.");
        }

        var segments = _Segments(uri.AbsolutePath);
        if (segments.Count == 0)
        {
            throw new FormatException("The repository URL has no repository path.");
        }

        var isHttp = scheme is "http" or "https";

        // Rebuild the URL git is handed. For HTTP(S), drop any userinfo so git falls back to the host credential
        // helper. For SSH (and any other scheme) keep the login user but strip any password after it — that
        // would otherwise reach argv, .git/config and the registry verbatim.
        var remoteUrl = isHttp
            ? _BuildHttpRemoteUrl(scheme, uri, segments)
            : _StripUrlPassword(url);

        return new GitCloneUrl(remoteUrl, uri.Host.ToLowerInvariant(), segments);
    }

    // Removes a "user:password@" password from a scheme URL's userinfo, keeping everything else verbatim.
    // String surgery rather than a Uri rebuild so an SSH path (absolute or ~-relative) reaches git unchanged.
    // The last '@' in the authority is the userinfo/host separator, so an '@' inside the password is handled too.
    private static string _StripUrlPassword(string url)
    {
        var schemeSep = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeSep < 0)
        {
            return url;
        }

        var authorityStart = schemeSep + 3;
        var authorityEnd = url.IndexOf('/', authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = url.Length;
        }

        var at = url.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
        if (at < 0)
        {
            return url;
        }

        var userInfo = url[authorityStart..at];
        var colon = userInfo.IndexOf(':');
        if (colon < 0)
        {
            return url;
        }

        var user = userInfo[..colon];
        return user.Length > 0
            ? string.Concat(url.AsSpan(0, authorityStart), user, url.AsSpan(at))
            : string.Concat(url.AsSpan(0, authorityStart), url.AsSpan(at + 1));
    }

    private static string _BuildHttpRemoteUrl(string scheme, Uri uri, IReadOnlyList<string> segments)
    {
        var builder = new StringBuilder(scheme).Append("://").Append(uri.Host.ToLowerInvariant());
        if (!uri.IsDefaultPort && uri.Port >= 0)
        {
            builder.Append(':').Append(uri.Port);
        }

        foreach (var segment in segments)
        {
            builder.Append('/').Append(segment);
        }

        return builder.ToString();
    }

    // Splits a repository path into sanitized, lowercased segments, dropping a trailing ".git" on the last one.
    // Lowercased so a host that treats case as equal does not clone the same repository twice under two folders;
    // sanitized so nothing an operator pastes can escape the managed root. Empty when the path names no repository.
    private static List<string> _Segments(string path)
    {
        var raw = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var segments = new List<string>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var value = raw[i];
            if (i == raw.Length - 1 && value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^4];
            }

            var sanitized = _Sanitize(value);
            if (sanitized.Length > 0)
            {
                segments.Add(sanitized);
            }
        }

        return segments;
    }

    private static string _Sanitize(string segment)
    {
        var builder = new StringBuilder(segment.Length);
        foreach (var character in segment)
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        // A segment that sanitizes to only dots ("." / "..") would be a path-traversal foothold; drop it entirely.
        var result = builder.ToString().Trim('-');
        return result.All(character => character == '.') ? string.Empty : result;
    }
}
