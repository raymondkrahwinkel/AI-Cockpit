using System.Text;

namespace Cockpit.Infrastructure.Clones;

/// <summary>
/// Parses a git remote URL into the pieces the clone manager needs (AC-90): the URL git is actually handed, the
/// managed slug/folder <c>host/org/repo</c> the clone lives under, and a normalized identity two URLs are compared
/// by for de-duplication. A pure value with no I/O, so the fiddly parsing — scp-style SSH, nested groups, a trailing
/// <c>.git</c>, credentials in an HTTPS URL — is testable on its own.
/// </summary>
/// <remarks>
/// Security (a binding project rule): for an <c>http</c>/<c>https</c> URL any <c>user:password@</c> credentials are
/// stripped from <see cref="RemoteUrl"/> before it ever reaches git, the registry, or a log. A token in the URL
/// would otherwise land in <c>.git/config</c>, the process arguments and the logs — the exact leak the design
/// forbids — and dropping it forces git down the host credential-helper path instead, which is v1's whole auth
/// model. An SSH URL keeps its <c>git@</c> user — that is the SSH login the clone needs, not a secret — but any
/// password after it (<c>user:secret@host</c>) is stripped just the same, so no scheme smuggles a credential through.
/// </remarks>
internal sealed class GitCloneUrl
{
    private GitCloneUrl(string remoteUrl, string host, IReadOnlyList<string> segments)
    {
        RemoteUrl = remoteUrl;
        Host = host;
        Segments = segments;
    }

    /// <summary>The URL git is handed — credentials stripped for HTTPS, so no secret reaches argv or <c>.git/config</c>.</summary>
    public string RemoteUrl { get; }

    /// <summary>The host the repository lives on, lowercased (<c>github.com</c>), the first slug segment.</summary>
    public string Host { get; }

    /// <summary>The path segments under the host — <c>[org, repo]</c>, or more for a nested group — sanitized and lowercased.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>The stable slug <c>host/org/repo</c> used for the managed folder and to name the clone.</summary>
    public string Slug => string.Join('/', new[] { Host }.Concat(Segments));

    /// <summary>The slug as an OS-native relative path (<c>host/org/repo</c> under the clones root).</summary>
    public string RelativePath => System.IO.Path.Combine(new[] { Host }.Concat(Segments).ToArray());

    /// <summary>
    /// The identity two remotes are the same repository by: host plus path, lowercased, scheme/credentials/port and
    /// a trailing <c>.git</c> ignored — so <c>https://github.com/o/r.git</c> and <c>git@github.com:o/r</c> match.
    /// </summary>
    public string NormalizedKey => Slug;

    /// <summary>
    /// Parses <paramref name="url"/>, or throws when it is not a git URL a clone could be built from — an empty
    /// string, no host, or no repository name. The message is safe to surface: it never echoes the raw URL, which
    /// could carry a token.
    /// </summary>
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

    /// <summary>Whether <paramref name="otherRemoteUrl"/> (an existing checkout's <c>origin</c>) is the same repository as this one — the de-dup test. A remote that will not parse is treated as "not the same", the safe direction.</summary>
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

        // Rebuild the URL git is handed. For HTTP(S), drop any userinfo — a token there is the forbidden leak — so
        // git falls back to the host credential helper. For SSH (and any other scheme) keep the login user but strip
        // any password after it: the git@ user is the SSH login, not a secret, whereas a "user:secret@host" password
        // would otherwise reach argv, .git/config and the registry verbatim.
        var remoteUrl = isHttp
            ? _BuildHttpRemoteUrl(scheme, uri, segments)
            : _StripUrlPassword(url);

        return new GitCloneUrl(remoteUrl, uri.Host.ToLowerInvariant(), segments);
    }

    // Removes a "user:password@" password from a scheme URL's userinfo while keeping the login user and everything
    // else — the repository path included — verbatim. String surgery rather than a Uri rebuild so an SSH path (which
    // may be absolute or ~-relative) is handed to git exactly as the operator gave it. The last '@' inside the
    // authority is the userinfo/host separator, so a password that itself contains '@' is still cut correctly.
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
    // Lowercased so a host that treats case as equal (GitHub) does not clone the same repository twice under two
    // folders; sanitized so nothing an operator pastes can escape the managed root (no "..", no separators inside a
    // segment). Empty when the path names no repository.
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
