namespace Cockpit.Core.Plugins;

/// <summary>
/// Resolves a store URL a user enters into the absolute <c>index.json</c> URL, and resolves a version's
/// repo-relative zip path against it (#14). Auto-detects the three shapes: a GitHub repo URL
/// (<c>github.com/owner/repo</c>, optionally <c>/tree/branch</c>) → the raw <c>index.json</c>; a direct
/// <c>.json</c> URL → itself; any other http(s) URL → treated as the base directory holding <c>index.json</c>.
/// </summary>
public static class PluginStoreUrl
{
    public static bool TryResolveIndexUrl(string entered, out string indexUrl, out string? error)
    {
        indexUrl = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(entered))
        {
            error = "Enter a store URL.";
            return false;
        }

        var url = entered.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Enter an http(s) URL, or a github.com/owner/repo URL.";
            return false;
        }

        // A GitHub repo URL → the raw index.json on its (branch, default main).
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                error = "That GitHub URL needs an owner and a repository.";
                return false;
            }

            var owner = segments[0];
            var repo = segments[1];
            var branch = segments.Length >= 4 && segments[2] == "tree" ? segments[3] : "main";
            indexUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/index.json";
            return true;
        }

        // A direct link to the index file.
        if (url.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            indexUrl = url;
            return true;
        }

        // Otherwise a base directory holding index.json.
        indexUrl = url.EndsWith('/') ? url + "index.json" : url + "/index.json";
        return true;
    }

    /// <summary>Resolves a version's repo-relative zip path against the index URL into an absolute download URL.</summary>
    public static string ResolveZipUrl(string indexUrl, string relativePath) =>
        new Uri(new Uri(indexUrl), relativePath).ToString();

    /// <summary>
    /// Parses a <c>github.com/owner/repo</c> URL (optionally <c>/tree/branch</c>) into its parts (AC-7). A
    /// private store is fetched through the authenticated Contents API rather than <c>raw.githubusercontent.com</c>,
    /// which does not serve a private repo with a bearer token; this is how the client knows it is a GitHub repo
    /// and on which branch.
    /// </summary>
    public static bool TryParseGitHubRepo(string entered, out string owner, out string repo, out string branch)
    {
        owner = string.Empty;
        repo = string.Empty;
        branch = "main";

        if (string.IsNullOrWhiteSpace(entered)
            || !Uri.TryCreate(entered.Trim(), UriKind.Absolute, out var uri)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        owner = segments[0];
        repo = segments[1];
        branch = segments.Length >= 4 && segments[2] == "tree" ? segments[3] : "main";

        return true;
    }

    /// <summary>
    /// The GitHub Contents API URL for a repo-relative file (AC-7), fetched with an Authorization header and
    /// <c>Accept: application/vnd.github.raw</c> so it returns the raw bytes rather than the metadata envelope.
    /// Each path segment is URL-encoded so a store-index path cannot inject a query (<c>?</c>/<c>#</c>) or otherwise
    /// escape the <c>contents/</c> route; validate the path with <see cref="IsSafeRelativePath"/> first.
    /// </summary>
    public static string GitHubContentsUrl(string owner, string repo, string relativePath, string branch)
    {
        var encoded = string.Join('/', relativePath.TrimStart('/').Split('/').Select(Uri.EscapeDataString));

        return $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/contents/{encoded}?ref={Uri.EscapeDataString(branch)}";
    }

    /// <summary>
    /// Whether a path taken from a store's <c>index.json</c> is a safe repo-relative path (AC-7): no scheme, no
    /// protocol-relative host, and no parent-directory escape. A store index is untrusted input, so a version's
    /// zip/template path must not be able to reach another repo (on the GitHub Contents API) or a foreign host.
    /// </summary>
    public static bool IsSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.StartsWith("//", StringComparison.Ordinal)
            || relativePath.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        return !relativePath.Split('/', '\\').Contains("..");
    }

    /// <summary>
    /// A short, human-readable name for a store URL, for showing it before — or when — its <c>index.json</c>
    /// advertises no <see cref="PluginStoreIndex.Name"/>: <c>owner/repo</c> for a GitHub repo URL or its raw
    /// <c>index.json</c>, otherwise the host. Never throws: an unparseable value falls back to itself.
    /// </summary>
    public static string DeriveDisplayName(string storeUrl)
    {
        if (string.IsNullOrWhiteSpace(storeUrl))
        {
            return "Unknown store";
        }

        var trimmed = storeUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var isGitHub = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

        return isGitHub && segments.Length >= 2 ? $"{segments[0]}/{segments[1]}" : uri.Host;
    }
}
