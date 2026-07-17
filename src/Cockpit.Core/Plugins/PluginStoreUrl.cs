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
