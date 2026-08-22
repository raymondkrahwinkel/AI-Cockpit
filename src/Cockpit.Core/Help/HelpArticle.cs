namespace Cockpit.Core.Help;

// One documentation page as it came out of an assembly's embedded resources. Everything the tree, the
// search and the renderer need is here; nothing about it is registered by hand, which is the point —
// adding a page is adding a file, and fixing a typo is editing that file and nothing else (AC-1033).
public sealed record HelpArticle
{
    // Unqualified, taken from the file name: `welcome.md` and `welcome.nl.md` are both `welcome`. Ids do
    // not translate, so a deep link lands on the same page whichever language file is being shown.
    public required string Key { get; init; }

    // What a deep link names. The core's pages keep their bare key; a plugin's are prefixed with its id,
    // so two plugins can both ship a `setup` page without one shadowing the other.
    public required string Id { get; init; }

    public required HelpOwner Owner { get; init; }

    public required HelpCategory Category { get; init; }

    public required string Title { get; init; }

    // One line for the overview card. Optional: a page with nothing worth summarising should not be made
    // to invent something.
    public string? Summary { get; init; }

    // Position within its own branch, ascending; equal values fall back to the title so the order of two
    // pages that forgot to declare one is at least stable between runs.
    public int Order { get; init; }

    public string? Icon { get; init; }

    // The body with the front matter stripped, as the renderer receives it.
    public required string Markdown { get; init; }

    // What stands above the first anchored heading. Rendered as the page's opening, and kept apart from the
    // sections so the window can draw one control per section and land a deep link on the right one.
    public required string Lead { get; init; }

    public required IReadOnlyList<HelpSection> Sections { get; init; }

    // The whole page as plain words, so a search can match text that sits above the first anchored heading —
    // an article whose opening paragraph holds the answer should still be findable.
    public required string PlainText { get; init; }

    // The language actually shown, which is not always the one that was asked for — see `IsTranslationMissing`.
    public required string Language { get; init; }

    // True when this page exists only in the default language while the operator asked for another one. The
    // window says so above the text: showing the English page silently would look like a translation that
    // simply reads oddly, and an empty page would look like a bug.
    public bool IsTranslationMissing { get; init; }

    // The resource-name prefix the article's own images live under, so a relative `![](images/x.png)`
    // resolves against the folder the markdown file came from rather than against the assembly root.
    public required string ResourcePrefix { get; init; }
}
