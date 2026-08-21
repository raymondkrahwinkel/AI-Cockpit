using System.Text.RegularExpressions;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-977: three rounds of manual scanning (AC-900, AC-970, AC-977 itself) each missed strings the previous
// round left behind. This replaces "scan by hand and claim it was clean" with a check that runs every build.
public class UiStringLanguageTests
{
    private static readonly string[] UiStringProperties = ["Content", "PlaceholderText", "Header", "Text", "Watermark", "ToolTip.Tip"];

    // Word-boundary Dutch function words and verb forms that do not also read as English words, so a hit is
    // Dutch and not a false positive on ordinary English UI copy.
    private static readonly Regex DutchWord = new(
        @"\b(de|het|een|en|van|voor|met|niet|wordt|worden|moet|moeten|kan|kunnen|deze|dat|dit|geen|naar|bij|aan|" +
        @"zijn|heeft|hebben|om|wil|wilt|maar|dus|omdat|toch|nu|alleen|nog|altijd|nooit|hier|daar|waar|wanneer|" +
        @"hoe|waarom|welke|andere|nieuwe|gelezen|leest|hieronder|boven|onder|links|rechts|klik|klikken|sleep|" +
        @"slepen|verwijder|verwijderen|toevoegen|toegevoegd|opslaan|opgeslagen|annuleren|sluiten|gesloten|openen|" +
        @"geopend|fout|foutmelding|waarschuwing|mislukt|gelukt|geslaagd|bewerken|bewerkt|wijzigen|gewijzigd|" +
        @"titel|naam|omschrijving|beschrijving|scherm|rechthoek|afgerond|ruit|stadion|optioneel|verplicht|leeg|" +
        @"ongeldig|geldig|plakken|knippen|ongedaan|opnieuw|terug|volgende|vorige|stap|stappen)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StringLiteralAssignment = new(
        $@"(?:{string.Join("|", UiStringProperties.Select(Regex.Escape))})\s*=\s*\$?""((?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    public static TheoryData<string> PluginSourceFiles()
    {
        var pluginDir = _PluginSourceDirectory();
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(pluginDir, "*.cs", SearchOption.AllDirectories))
        {
            data.Add(Path.GetRelativePath(pluginDir, file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PluginSourceFiles))]
    public void UiStringLiterals_ContainNoDutchWords(string relativePath)
    {
        var path = Path.Combine(_PluginSourceDirectory(), relativePath);
        var lines = File.ReadAllLines(path);

        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match assignment in StringLiteralAssignment.Matches(lines[i]))
            {
                var value = assignment.Groups[1].Value;
                var hit = DutchWord.Match(value);
                Assert.False(hit.Success, $"{relativePath}:{i + 1} looks Dutch (\"{hit.Value}\" in \"{value}\")");
            }
        }
    }

    // Repo file rather than a build artefact, so it is found by walking up from wherever the test binary landed —
    // same lookup WireframeVocabularyTests uses for docs/wireframe-format.md.
    private static string _PluginSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "plugins-dev", "Cockpit.Plugin.Diagram")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "plugins-dev", "Cockpit.Plugin.Diagram");
    }
}
