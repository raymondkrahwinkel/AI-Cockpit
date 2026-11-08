using Cockpit.Core.Abstractions.Mentions;

namespace Cockpit.App.ViewModels;

// One row of the @-mention list: a group heading (Item is null) or a pickable item, its title split around the query hit.
public sealed record MentionMatch(string Key, MentionItem? Item, string Heading, string TitleBefore, string TitleHit, string TitleAfter)
{
    public static MentionMatch ForHeading(MentionGroup group) =>
        new("heading:" + group.Title, null, group.Subtitle is null ? group.Title : $"{group.Title} · {group.Subtitle}", "", "", "");

    public static MentionMatch ForItem(MentionItem item, string query)
    {
        var at = query.Length == 0 ? -1 : item.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        return at < 0
            ? new(item.Key, item, "", item.Title, "", "")
            : new(item.Key, item, "", item.Title[..at], item.Title.Substring(at, query.Length), item.Title[(at + query.Length)..]);
    }

    public bool IsHeading => Item is null;

    public bool IsItem => Item is not null;

    public bool IsFile => Item?.Kind == MentionKind.File;

    public string SourceLine => Item?.SourceLine ?? "";

    public DateTimeOffset? LastActivity => Item?.LastActivity;

    public MentionTile? Tile => Item?.Tile;
}
