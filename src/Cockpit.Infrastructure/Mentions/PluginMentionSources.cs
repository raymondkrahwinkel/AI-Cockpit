using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mentions;
using Cockpit.Plugins.Abstractions.Mentions;

namespace Cockpit.Infrastructure.Mentions;

// The mention sources plugins register, shown as one group; a source that throws or misses the deadline drops out alone.
internal sealed class PluginMentionSources : IMentionSource, ISingletonService
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, IPluginMentionSource> _sources = new(StringComparer.Ordinal);

    public string Key => "plugins";

    public int Order => 20;

    public bool Register(IPluginMentionSource source)
    {
        lock (_sources)
        {
            return !string.IsNullOrWhiteSpace(source.Key) && _sources.TryAdd(source.Key, source);
        }
    }

    public void Remove(string key)
    {
        lock (_sources)
        {
            _sources.Remove(key);
        }
    }

    public async Task<IReadOnlyList<MentionGroup>> GetAsync(MentionContext context, CancellationToken cancellationToken)
    {
        List<IPluginMentionSource> sources;
        lock (_sources)
        {
            sources = [.. _sources.Values];
        }

        var rows = await Task.WhenAll(sources.Select(source => _RowsAsync(source, cancellationToken)));
        var items = rows.SelectMany(row => row).ToList();
        return items.Count == 0 ? [] : [new MentionGroup("Suggested by plugins", items)];
    }

    private static async Task<IReadOnlyList<MentionItem>> _RowsAsync(IPluginMentionSource source, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        try
        {
            // Task.Run so a source that blocks before its first await cannot hold up the others; WaitAsync for one that ignores the token.
            var mentions = await Task.Run(() => source.GetAsync(string.Empty, deadline.Token), deadline.Token).WaitAsync(deadline.Token);
            var via = "via " + source.SourceName;
            return [.. (mentions ?? []).Select(mention => new MentionItem(
                $"plugin:{source.Key}:{mention.Reference}", mention.Title,
                string.IsNullOrEmpty(mention.SourceLine) ? via : $"{mention.SourceLine} · {via}",
                new MentionTile(mention.Title.Length > 0 ? mention.Title[..1].ToUpperInvariant() : "?", LogoBytes: source.SourceLogo),
                MentionKind.Suggestion, mention.InsertText, mention.Reference, mention.LastActivity))];
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }
}
