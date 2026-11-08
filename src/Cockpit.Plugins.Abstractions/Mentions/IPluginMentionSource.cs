namespace Cockpit.Plugins.Abstractions.Mentions;

/// <summary>
/// A plugin's own suggestions for the chat's @-list, registered through <see cref="ICockpitHost.AddMentionSource"/>.
/// </summary>
public interface IPluginMentionSource
{
    /// <summary>
    /// Stable key this source is registered and removed under (<see cref="ICockpitHost.RemoveMentionSource"/>).
    /// </summary>
    string Key { get; }

    /// <summary>
    /// The source's name, shown as "via <c>SourceName</c>" on each of its rows.
    /// </summary>
    string SourceName { get; }

    /// <summary>
    /// Optional logo bytes for this source's rows; null uses a letter tile.
    /// </summary>
    byte[]? SourceLogo => null;

    /// <summary>
    /// The suggestions for <paramref name="query"/>, the text typed after the '@' (empty when the list just opened).
    /// </summary>
    /// <remarks>
    /// Must not throw for an ordinary failure: return an empty list. The host drops a source that throws or is not
    /// done within two seconds, without costing any other source its rows.
    /// </remarks>
    Task<IReadOnlyList<PluginMention>> GetAsync(string query, CancellationToken cancellationToken);
}
