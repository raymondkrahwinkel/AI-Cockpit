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
        var (lead, sections) = _Split(body);

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
            Lead = lead,
            Sections = sections,
            PlainText = _PlainText(body),
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

    // Every heading carrying an explicit `{#id}` opens a section that runs until the next one; what comes
    // before the first of them is the article's lead. A heading without an id is ordinary prose belonging to
    // the section it sits in: it reads the same, it just cannot be linked to, which is the author's decision
    // rather than a generated anchor that silently moves the next time the wording changes.
    //
    // Split over the source lines rather than the parsed blocks, because the window renders one control per
    // section and needs each one's markdown back, not only its words. Fences are tracked so a `##` line inside
    // a code sample — which the plugin-writing pages are full of — does not open a section of its own.
    private static (string Lead, IReadOnlyList<HelpSection> Sections) _Split(string body)
    {
        var sections = new List<HelpSection>();
        var lead = new StringBuilder();
        var current = new StringBuilder();
        string? id = null;
        var title = string.Empty;
        var fenced = false;

        void Flush()
        {
            if (id is not null)
            {
                var markdown = current.ToString().TrimEnd();

                // The plain text leaves the heading line out while the markdown keeps it: the heading is
                // already the hit's title on screen, and repeating it as the first words of every snippet
                // wastes the one line a result gets to say something the title did not.
                var newline = markdown.IndexOf('\n');
                var withoutHeading = newline < 0 ? string.Empty : markdown[(newline + 1)..];

                sections.Add(new HelpSection(id, title, _PlainText(withoutHeading), markdown));
            }
        }

        foreach (var line in body.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }

            var heading = fenced ? System.Text.RegularExpressions.Match.Empty : SectionHeadingRegex().Match(line);
            if (heading.Success)
            {
                Flush();
                id = heading.Groups[2].Value;
                title = heading.Groups[1].Value.Trim();
                current.Clear();
                current.AppendLine(line);
                continue;
            }

            (id is null ? lead : current).AppendLine(line);
        }

        Flush();
        return (lead.ToString().Trim(), sections);
    }

    private static string _PlainText(string markdown) =>
        string.Join('\n', MarkdownParser.Parse(markdown).Select(_PlainText));

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

    // A heading that declares an anchor, which is the only kind that opens an addressable section.
    [GeneratedRegex(@"^#{1,6}\s+(.*?)\s*\{#([A-Za-z0-9._-]+)\}\s*$")]
    private static partial Regex SectionHeadingRegex();

    private sealed record _DocFile(string Key, string Language, string ResourceName, string ResourcePrefix);
}
