namespace Cockpit.Core.Abstractions.Mentions;

// A source of groups for the @-mention list; the list shows the groups of every registered source in Order.
public interface IMentionSource
{
    string Key { get; }

    int Order { get; }

    Task<IReadOnlyList<MentionGroup>> GetAsync(MentionContext context, CancellationToken cancellationToken);
}

public enum MentionSurface
{
    Home,
    Session,
    Assistant,
}

public sealed record MentionContext(MentionSurface Surface, string? WorkingDirectory);

// Subtitle is the "via <source>" line under the title, when the group comes from a plugin.
public sealed record MentionGroup(string Title, IReadOnlyList<MentionItem> Items, string? Subtitle = null);

public enum MentionKind
{
    File,
    Project,
    Suggestion,
}

// Letter is the fallback when there is no logo; Logo is a path or bytes, whichever the source has.
public sealed record MentionTile(string Letter, string? LogoPath = null, byte[]? LogoBytes = null);

// InsertText is what follows the '@'; Reference is the local path, or the shared id as "<scheme>:<value>".
public sealed record MentionItem(
    string Key, string Title, string SourceLine, MentionTile Tile, MentionKind Kind, string InsertText, string? Reference,
    DateTimeOffset? LastActivity = null);
