using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Cockpit.Core.Markdown;

namespace Cockpit.Core.Help;

// Turns an assembly's embedded resources into documentation pages. Discovery is by convention — everything
// under a `Docs` folder that made it into the assembly — and never by a list, because a list is a second
// source that drifts from the files it names and has to be remembered on every edit. Adding a page is
// adding a file; fixing a typo is editing that file (AC-1033).
//
// The same scanner runs for the app's own assembly and for a plugin's. That is the whole point: if the core
// documentation took a different path, this would be a plugin feature with an exception beside it rather
// than a documentation system.
public static partial class HelpDocumentScanner
{
    // MSBuild flattens `Docs\welcome.md` into `<RootNamespace>.Docs.welcome.md`, so this segment is what
    // marks a resource as documentation regardless of what the assembly is called.
    public const string FolderMarker = ".Docs.";

    public const string DefaultLanguage = "en";

    public static IReadOnlyList<HelpArticle> Scan(Assembly assembly, HelpOwner owner, string? language = null)
    {
        var wanted = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.Trim();
        var files = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Select(_Describe)
            .Where(file => file is not null)
            .Select(file => file!)
            .ToList();

        var articles = new List<HelpArticle>();
        foreach (var group in files.GroupBy(file => file.Key, StringComparer.OrdinalIgnoreCase))
        {
            var chosen = _Choose(group, wanted);
            var article = _Read(assembly, owner, chosen, wanted);
            if (article is not null)
            {
                articles.Add(article);
            }
        }

        return articles
            .OrderBy(article => article.Order)
            .ThenBy(article => article.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // The file actually shown: the operator's language when it exists, otherwise the default one. Falling
    // back is visible rather than silent — see `HelpArticle.IsTranslationMissing` — because an English page
    // presented as if it were the translation reads like a bad translation, and a blank one reads like a bug.
    private static _DocFile _Choose(IEnumerable<_DocFile> candidates, string language)
    {
        var files = candidates.ToList();

        return files.FirstOrDefault(file => string.Equals(file.Language, language, StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(file => string.Equals(file.Language, DefaultLanguage, StringComparison.OrdinalIgnoreCase))
            ?? files[0];
    }

    private static _DocFile? _Describe(string resourceName)
    {
        var marker = resourceName.IndexOf(FolderMarker, StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        var prefix = resourceName[..(marker + FolderMarker.Length)];
        var relative = resourceName[(marker + FolderMarker.Length)..];
        var stem = relative[..^".md".Length];

        // `welcome.nl.md` is the Dutch `welcome`; `welcome.md` is the default language and stays valid, so a
        // plugin that never translates anything writes no locale anywhere. The separator is a dot and not a
        // dash because article names contain dashes themselves — `getting-started-en` would be ambiguous.
        var lastDot = stem.LastIndexOf('.');
        var suffix = lastDot < 0 ? string.Empty : stem[(lastDot + 1)..];

        return LanguageTagRegex().IsMatch(suffix)
            ? new _DocFile(stem[..lastDot], suffix, resourceName, prefix)
            : new _DocFile(stem, DefaultLanguage, resourceName, prefix);
    }

    private static HelpArticle? _Read(Assembly assembly, HelpOwner owner, _DocFile file, string wanted)
    {
        using var stream = assembly.GetManifestResourceStream(file.ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var front = HelpFrontMatter.Parse(reader.ReadToEnd(), out var body);
        var blocks = MarkdownParser.Parse(body);

        return new HelpArticle
        {
            Key = file.Key,
            Id = owner.IsCore ? file.Key : $"{owner.Id}/{file.Key}",
            Owner = owner,
            Category = _Category(owner, front.Category),
            Title = front.Title ?? file.Key,
            Summary = front.Summary,
            Order = front.Order,
            Icon = front.Icon,
            Markdown = body,
            Sections = _Sections(blocks),
            PlainText = string.Join('\n', blocks.Select(_PlainText)),
            Language = file.Language,
            IsTranslationMissing = !string.Equals(file.Language, wanted, StringComparison.OrdinalIgnoreCase),
            ResourcePrefix = file.ResourcePrefix,
        };
    }

    // A plugin's pages always land under `Plugins`, whatever its front matter says. Ignored rather than
    // rejected: an author who tries the key gets the documented behaviour instead of a page that quietly
    // fails to appear anywhere.
    private static HelpCategory _Category(HelpOwner owner, string? declared)
    {
        if (!owner.IsCore)
        {
            return HelpCategory.Plugins;
        }

        return declared?.Trim().ToLowerInvariant() switch
        {
            "system" => HelpCategory.System,
            "extending" or "extending-cockpit" => HelpCategory.ExtendingCockpit,
            _ => HelpCategory.General,
        };
    }

    // Every heading carrying an explicit `{#id}` opens a section that runs until the next one. A heading
    // without an id is ordinary prose that belongs to the section it sits in: it reads the same, it just
    // cannot be linked to, which is the author's decision rather than a generated anchor that silently
    // moves the next time the wording changes.
    private static IReadOnlyList<HelpSection> _Sections(IReadOnlyList<MarkdownBlock> blocks)
    {
        var sections = new List<HelpSection>();
        string? id = null;
        var title = string.Empty;
        var text = new StringBuilder();

        void Flush()
        {
            if (id is not null)
            {
                sections.Add(new HelpSection(id, title, text.ToString().Trim()));
            }
        }

        foreach (var block in blocks)
        {
            if (block is { Kind: MarkdownBlockKind.Heading, HeadingId: not null })
            {
                Flush();
                id = block.HeadingId;
                title = _PlainText(block);
                text.Clear();
                continue;
            }

            text.AppendLine(_PlainText(block));
        }

        Flush();
        return sections;
    }

    private static string _PlainText(MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.CodeBlock => block.Code,
        MarkdownBlockKind.Image => block.ImageAlt ?? string.Empty,
        MarkdownBlockKind.List => string.Join('\n', block.Items.Select(_Runs)),
        MarkdownBlockKind.Table => string.Join(
            '\n',
            block.Items.Select(_Runs).Concat(block.Rows.Select(row => string.Join(' ', row.Select(_Runs))))),
        _ => _Runs(block.Inlines),
    };

    private static string _Runs(IReadOnlyList<MarkdownInline> inlines) =>
        string.Concat(inlines.Select(inline => inline.Text));

    // Two letters, optionally with a region — the ISO-639-1 shape the voice layer already uses. Kept this
    // tight on purpose: a looser rule turns the last segment of a nested `Docs/api/ref.md` into the language
    // "ref" and hides the page from everyone.
    [GeneratedRegex(@"^[A-Za-z]{2}(-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex LanguageTagRegex();

    private sealed record _DocFile(string Key, string Language, string ResourceName, string ResourcePrefix);
}
