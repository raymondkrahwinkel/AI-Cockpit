using Avalonia.Media.Imaging;

namespace Cockpit.Plugin.Browser.UI;

// A tab's icon, fetched once per URL with a plain HTTP GET (no cookies, outside the profile) and kept in memory for the
// process. Anything that is not a small http(s) image, or fails to decode, is simply no icon.
// ponytail: unbounded per-process cache, fine for a browsing session's handful of hosts; add eviction if it ever grows.
internal static class Favicons
{
    private const int MaxBytes = 256 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Dictionary<string, Task<Bitmap?>> Cache = [];

    // UI thread only, like every caller: the cache needs no lock.
    public static Task<Bitmap?> GetAsync(string url)
    {
        if (Cache.TryGetValue(url, out var cached))
        {
            return cached;
        }

        var fetch = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? _FetchAsync(uri)
            : Task.FromResult<Bitmap?>(null);
        Cache[url] = fetch;
        return fetch;
    }

    private static async Task<Bitmap?> _FetchAsync(Uri uri)
    {
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (response.IsSuccessStatusCode == false || response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            return bytes.Length > MaxBytes ? null : new Bitmap(new MemoryStream(bytes));
        }
        // A network failure or an image the decoder refuses (an SVG, a broken ICO) means no icon, never a fault.
        catch (Exception)
        {
            return null;
        }
    }
}
