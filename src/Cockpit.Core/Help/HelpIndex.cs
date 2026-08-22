using System.Reflection;

namespace Cockpit.Core.Help;

// AC-1033: every page the app and its plugins shipped, in one index rather than one per component — you do
// not know which component holds the answer at the moment you need it. Rebuilt, never patched.
public sealed class HelpIndex
{
    private readonly Dictionary<string, HelpArticle> _byId;
    private readonly Dictionary<string, _Resources> _byOwner;

    private HelpIndex(IReadOnlyList<HelpArticle> articles, Dictionary<string, _Resources> byOwner)
    {
        Articles = articles;
        _byOwner = byOwner;
        _byId = articles.ToDictionary(article => article.Id, StringComparer.OrdinalIgnoreCase);
    }

    public static HelpIndex Empty { get; } = new([], []);

    public IReadOnlyList<HelpArticle> Articles { get; }

    public static HelpIndex Build(IEnumerable<HelpDocumentSource> sources, string? language = null)
    {
        var articles = new List<HelpArticle>();
        var owners = new Dictionary<string, _Resources>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var scanned = HelpDocumentScanner.Scan(source.Assembly, source.Owner, language);
            if (scanned.Count == 0)
            {
                continue;
            }

            articles.AddRange(scanned);
            owners[source.Owner.Id] = new _Resources(
                source.Assembly,
                source.Assembly.GetManifestResourceNames().ToDictionary(name => name, StringComparer.OrdinalIgnoreCase));
        }

        return new HelpIndex(articles, owners);
    }

    public HelpArticle? Find(string? articleId) =>
        articleId is not null && _byId.TryGetValue(articleId, out var article) ? article : null;

    // What a `?` asks before it draws itself, and what the deep-link test asserts for every target in the
    // codebase. A question mark that opens nothing is worse than no question mark, so the element has to be
    // able to find out whether its target exists before it offers itself.
    public bool Contains(HelpAddress? address)
    {
        var article = Find(address?.Article);
        if (article is null)
        {
            return false;
        }

        return address!.Section is null
            || article.Sections.Any(section => string.Equals(section.Id, address.Section, StringComparison.OrdinalIgnoreCase));
    }

    public HelpSection? FindSection(HelpAddress address) =>
        Find(address.Article)?.Sections
            .FirstOrDefault(section => string.Equals(section.Id, address.Section, StringComparison.OrdinalIgnoreCase));

    // Offline by construction: this walks text the app already has in memory. A search that needed a service
    // would undo the reason the documentation is in the app at all.
    public IReadOnlyList<HelpSearchHit> Search(string? query, int limit = 25)
    {
        var terms = (query ?? string.Empty)
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (terms.Count == 0)
        {
            return [];
        }

        var hits = new List<HelpSearchHit>();
        foreach (var article in Articles)
        {
            hits.AddRange(_Hits(article, terms));
        }

        return hits
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Article.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToList();
    }

    // Resolves one `![](...)` reference against the assembly the page was shipped in, and nowhere else. An
    // address with a scheme is refused without being fetched: opening a page from a plugin you did not write
    // must not be the moment a stranger's server learns your IP address.
    public HelpImage LoadImage(HelpArticle article, string? source, bool dark = false)
    {
        var reference = source?.Trim();
        if (string.IsNullOrEmpty(reference))
        {
            return HelpImage.Missing;
        }

        if (reference.Contains("://", StringComparison.Ordinal)
            || reference.StartsWith("//", StringComparison.Ordinal)
            || reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return HelpImage.Blocked;
        }

        if (!_byOwner.TryGetValue(article.Owner.Id, out var resources))
        {
            return HelpImage.Missing;
        }

        var relative = reference.Replace('\\', '/').TrimStart('/').Replace('/', '.');

        // A light and a dark screenshot of the same UI are both right and neither works in the other theme,
        // so a `foo.dark.png` beside `foo.png` is honoured when it exists. Optional, never required: one
        // image has to remain the ordinary case.
        return (dark ? _Read(resources, article.ResourcePrefix + _DarkVariant(relative)) : null)
            ?? _Read(resources, article.ResourcePrefix + relative)
            ?? HelpImage.Missing;
    }

    private static string _DarkVariant(string relative)
    {
        var dot = relative.LastIndexOf('.');
        return dot < 0 ? relative + ".dark" : relative[..dot] + ".dark" + relative[dot..];
    }

    private static HelpImage? _Read(_Resources resources, string resourceName)
    {
        if (!resources.Names.TryGetValue(resourceName, out var actual))
        {
            return null;
        }

        using var stream = resources.Assembly.GetManifestResourceStream(actual);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return new HelpImage(HelpImageOutcome.Embedded, buffer.ToArray());
    }

    private static IEnumerable<HelpSearchHit> _Hits(HelpArticle article, List<string> terms)
    {
        foreach (var section in article.Sections)
        {
            var score = _Score(section.Title, section.Text, terms);
            if (score > 0)
            {
                yield return new HelpSearchHit(article, section, _Snippet(section.Text, terms), score);
            }
        }

        // The page itself, so an answer sitting above the first anchored heading is still findable — and so
        // is a page whose title is what the operator typed even when the body never repeats it.
        var whole = _Score(article.Title, article.PlainText, terms);
        if (whole > 0)
        {
            yield return new HelpSearchHit(article, null, _Snippet(article.PlainText, terms), whole + 1);
        }
    }

    // Every term has to appear somewhere, and a term in the heading counts for more than one buried in the
    // text: the heading is what the section is about, the body is where it happens to be mentioned.
    private static int _Score(string title, string text, List<string> terms)
    {
        var total = 0;
        foreach (var term in terms)
        {
            var inTitle = title.Contains(term, StringComparison.CurrentCultureIgnoreCase);
            var inText = text.Contains(term, StringComparison.CurrentCultureIgnoreCase);
            if (!inTitle && !inText)
            {
                return 0;
            }

            total += (inTitle ? 5 : 0) + (inText ? 1 : 0);
        }

        return total;
    }

    private static string _Snippet(string text, List<string> terms)
    {
        var flat = string.Join(' ', text.Split(['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        var at = terms
            .Select(term => flat.IndexOf(term, StringComparison.CurrentCultureIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(0)
            .Min();

        var start = Math.Max(0, at - 40);
        var length = Math.Min(flat.Length - start, 180);
        var window = flat.Substring(start, length);

        return (start > 0 ? "…" : string.Empty) + window + (start + length < flat.Length ? "…" : string.Empty);
    }

    private sealed record _Resources(Assembly Assembly, Dictionary<string, string> Names);
}
