using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Discord;

/// <summary>
/// Downloads an attachment from Discord's CDN (AC-1049). Kept behind an interface so the bridge's own tests never
/// reach the network.
/// </summary>
internal interface IDiscordFileFetcher
{
    /// <summary>
    /// The bytes at <paramref name="url"/>, or an exception saying why they are not coming.
    /// </summary>
    Task<byte[]> FetchAsync(string url, CancellationToken cancellationToken = default);
}

// Unlike Slack's private file URLs, a Discord attachment sits on a plain CDN URL that needs no token and no extra
// scope — the one real difference between the two platforms here (AC-1049).
internal sealed class DiscordFileFetcher : IDiscordFileFetcher, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<byte[]> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

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
