namespace Cockpit.Plugin.Slack.Tests;

// Stands in for the network: a URL either has bytes waiting for it or throws, which is every case the bridge
// has to tell apart.
internal sealed class FakeSlackFileFetcher : ISlackFileFetcher
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public List<string> Fetched { get; } = [];

    public Task<byte[]> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        Fetched.Add(url);

        return Files.TryGetValue(url, out var bytes)
            ? Task.FromResult(bytes)
            : Task.FromException<byte[]>(new InvalidOperationException($"nothing at {url}"));
    }
}
