using System.Text.RegularExpressions;
using Cockpit.Core.Help;

namespace Cockpit.Core.Tests.Help;

/// <summary>
/// AC-1033, criterion 5: every deep link written anywhere in the codebase, checked against the documentation
/// actually in it. Turns a link gone stale from something a reader finds out into something the build says.
/// </summary>
public partial class HelpDeepLinkSweepTests
{
    // The shapes a target is written in: the three host members a plugin calls, and a HelpAddress built by hand.
    [GeneratedRegex(@"(?:CreateHelpHint|OpenHelp|HasHelp|new HelpAddress)\(\s*""([^""]+)""\s*(?:,\s*""([^""]*)"")?")]
    private static partial Regex TargetRegex();

    // A cross-reference inside a page: `[the panel](help:worktrees#the-panel)`.
    [GeneratedRegex(@"\]\(\s*help:([^)\s]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"^\s*<EmbeddedResource\s+(?:Include|Update)=""[^""]*Docs[^""]*""(?![^>]*WithCulture)", RegexOptions.Multiline)]
    private static partial Regex UnguardedDocsResourceRegex();

    [GeneratedRegex(@"^#{1,6}\s+.*\{#([A-Za-z0-9._-]+)\}\s*$", RegexOptions.Multiline)]
    private static partial Regex SectionIdRegex();

    [GeneratedRegex(@"""id""\s*:\s*""([^""]+)""")]
    private static partial Regex ManifestIdRegex();

    // Any link a page writes, and not a picture: an image reference resolves against the assembly rather than
    // against the set of pages, and has its own rules.
    [GeneratedRegex(@"(?<!!)\[[^\]]*\]\(([^)]+)\)")]
    private static partial Regex PageLinkRegex();

    [Fact]
    public void EveryDeepLinkInTheCodebaseResolves()
    {
        var known = _ShippedAddresses(includeOwnerRelative: true);
        var broken = new List<string>();
        var swept = 0;

        foreach (var file in _Sources("*.cs").Concat(_Sources("*.axaml")))
        {
            foreach (Match match in TargetRegex().Matches(File.ReadAllText(file)))
            {
                var section = match.Groups[2].Success && match.Groups[2].Value.Length > 0 ? match.Groups[2].Value : null;
                var address = new HelpAddress(match.Groups[1].Value, section).ToString();
                swept++;

                if (!known.Contains(address))
                {
                    broken.Add($"{Path.GetFileName(file)}: {address}");
                }
            }
        }

        Assert.Empty(broken);

        // A sweep that found nothing to check would pass forever while proving nothing — the exact shape of
        // failure it is here to catch. Both halves have to have found something for the green to mean anything.
        Assert.NotEmpty(known);
        Assert.True(swept > 0, "the sweep matched no deep links at all — it is not reading the source tree");
    }

    // AC-1033: a project spelling its own EmbeddedResource line without WithCulture="false" sends a
    // translation into a satellite assembly the scanner never reads, silently. The SDK's targets cover the
    // projects that do not hand-roll it.
    [Fact]
    public void EveryProjectThatEmbedsDocumentationKeepsItOutOfASatelliteAssembly()
    {
        var offenders = _Sources("*.csproj")
            .Where(file => UnguardedDocsResourceRegex().IsMatch(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    // AC-1040: the same sweep over the `help:` links inside the pages, which have no call site for the one
    // above to find, and checked as written because the window resolves them literally.
    [Fact]
    public void EveryCrossReferenceInsideAPageResolves()
    {
        var known = _ShippedAddresses(includeOwnerRelative: false);
        var broken = new List<string>();
        var swept = 0;

        foreach (var file in _Sources("*.md").Where(_IsDocumentation))
        {
            foreach (Match match in MarkdownLinkRegex().Matches(File.ReadAllText(file)))
            {
                var address = HelpAddress.Parse(match.Groups[1].Value).ToString();
                swept++;

                if (!known.Contains(address))
                {
                    broken.Add($"{Path.GetFileName(file)}: {address}");
                }
            }
        }

        Assert.Empty(broken);
        Assert.True(swept > 0, "no page cross-references anything — the sweep is not reading the documentation");
    }

    /// <summary>
    /// AC-1042: the plain markdown links the pages write to each other, checked the way the window resolves
    /// them. A guide that ships from `docs/` writes ordinary markdown so GitHub can follow it too — this is
    /// what keeps that one spelling honest in the app.
    /// </summary>
    [Fact]
    public void EveryLinkBetweenShippedPagesResolves()
    {
        // Both spellings: these arrive at `HelpService.Resolve`, which reads a plugin's link as its own page
        // first and as written second.
        var known = _ShippedAddresses(includeOwnerRelative: true);
        var broken = new List<string>();
        var swept = 0;

        foreach (var file in _Sources("*.md").Where(_IsDocumentation))
        {
            foreach (Match match in PageLinkRegex().Matches(File.ReadAllText(file)))
            {
                var target = match.Groups[1].Value.Trim();

                // A colon is a scheme — `https:`, `mailto:`, and the `help:` links the sweep above owns.
                if (target.Length == 0 || target.Contains(':'))
                {
                    continue;
                }

                var address = target.StartsWith('#')
                    ? new HelpAddress(_Key(file), target[1..])
                    : HelpAddress.FromSiblingLink(target) ?? new HelpAddress(target);

                swept++;
                if (!known.Contains(address.ToString()))
                {
                    broken.Add($"{Path.GetFileName(file)} → {target}");
                }
            }
        }

        Assert.Empty(broken);
        Assert.True(swept > 0, "no links between pages were checked — the sweep is not reading the pages");
    }

    // Every address the documentation in this repository answers to. `includeOwnerRelative` adds the bare
    // spelling a plugin uses for its own page, which the host prefixes on the way in — true for the call sites
    // that go through that resolution, false for a link written in a page, which is taken as it stands.
    private static HashSet<string> _ShippedAddresses(bool includeOwnerRelative)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in _Sources("*.md").Where(_IsDocumentation))
        {
            var text = File.ReadAllText(file);
            var key = _Key(file);
            var owner = _OwnerId(file);
            var sections = SectionIdRegex().Matches(text).Select(match => match.Groups[1].Value).ToList();

            string[] spellings = owner is null ? [key]
                : includeOwnerRelative ? [key, $"{owner}/{key}"]
                : [$"{owner}/{key}"];

            foreach (var article in spellings)
            {
                known.Add(article);
                foreach (var section in sections)
                {
                    known.Add($"{article}#{section}");
                }
            }
        }

        return known;
    }

    // The `README.md` beside the guides in `docs/plugins` is navigation for whoever opens that folder on
    // GitHub, and is the one file there the app does not ship — see Cockpit.App.csproj.
    private static bool _IsDocumentation(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}Docs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Path.GetFileName(file), "README.md", StringComparison.OrdinalIgnoreCase);

    // `welcome.md` and `welcome.nl.md` are both the article `welcome`: ids come from the file name and do not
    // translate, which is the rule that lets one deep link land in the same place in every language.
    private static string _Key(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var dot = stem.LastIndexOf('.');

        return dot > 0 && stem.Length - dot <= 4 ? stem[..dot] : stem;
    }

    // Null for the app's own pages, whose ids stay bare; a plugin's manifest id otherwise.
    private static string? _OwnerId(string file)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null)
        {
            var manifest = Path.Combine(directory.FullName, "plugin.json");
            if (File.Exists(manifest))
            {
                return ManifestIdRegex().Match(File.ReadAllText(manifest)) is { Success: true } match
                    ? match.Groups[1].Value
                    : null;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static IEnumerable<string> _Sources(string pattern)
    {
        var root = _RepositoryRoot();

        return root is null
            ? []
            // AC-1042: `docs/plugins` ships from where it lies rather than being copied into a `Docs` folder,
            // so the sweep has to look there too — `_IsDocumentation` matches its lower-case name already.
            : new[] { "src", "plugins-dev", Path.Combine("docs", "plugins") }
                .Select(folder => Path.Combine(root, folder))
                .Where(Directory.Exists)
                .SelectMany(folder => Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string? _RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        // A worktree's `.git` is a file rather than a directory, and this suite runs from one often enough
        // that only checking for the directory would quietly scan nothing and pass.
        while (directory is not null && !_IsRepositoryRoot(directory))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }

    private static bool _IsRepositoryRoot(DirectoryInfo directory)
    {
        var marker = Path.Combine(directory.FullName, ".git");

        return Directory.Exists(marker) || File.Exists(marker);
    }
}
