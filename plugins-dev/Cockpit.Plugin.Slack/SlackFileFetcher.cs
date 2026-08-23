using System.Net.Http.Headers;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Slack;

/// <summary>
/// Downloads a file Slack only serves to the bot (AC-1049). Kept behind an interface so the bridge's own tests
/// never reach the network.
/// </summary>
internal interface ISlackFileFetcher
{
    /// <summary>
    /// The bytes at <paramref name="url"/>, or an exception saying why they are not coming.
    /// </summary>
    Task<byte[]> FetchAsync(string url, CancellationToken cancellationToken = default);
}

// A Slack file URL is private: it needs the bot token in an Authorization header, and the app needs the
// `files:read` scope for that to mean anything (AC-1049). Without the scope Slack does not answer with an error
// — it answers 200 with its sign-in page — so the content type is checked here and a page is a failure, not bytes.
internal sealed class SlackFileFetcher : ISlackFileFetcher, IDisposable
{
    private readonly HttpClient _http;

    public SlackFileFetcher(string botToken)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
    }

    public async Task<byte[]> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Slack answered with {mediaType ?? "no content type"} instead of an image — the app is probably missing the files:read scope.");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCappedAsync(stream, AssistantChannelImageLimits.MaxBytes, cancellationToken).ConfigureAwait(false);
    }

    // Counted while reading rather than trusted from Content-Length, which is a claim the sender's side makes and
    // may simply be absent — the cap has to hold either way.
    internal static async Task<byte[]> ReadCappedAsync(Stream stream, int cap, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];

        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > cap)
            {
                throw new InvalidOperationException($"the file is bigger than {cap / (1024 * 1024)} MB");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
