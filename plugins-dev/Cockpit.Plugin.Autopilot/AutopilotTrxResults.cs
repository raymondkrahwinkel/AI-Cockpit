using System.Text.Json;
using System.Xml.Linq;

namespace Cockpit.Plugin.Autopilot;

// What a directory of TRX files says (AC-1341, EpicWorkflow §5): how many tests ran and which failed, by the name
// the logger wrote — the same name on the tip and on the baseline, so the two red sets are comparable as sets.
// `Sha` is the commit a recorded baseline says it ran on (§6d), from the manifest beside its TRX; null without one.
internal sealed record AutopilotTrxResults(int Files, int Total, int Passed, IReadOnlySet<string> FailedTests, string? Sha)
{
    // The manifest an operator leaves beside a baseline's TRX files: `{"sha": "<origin/main commit the suite ran on>"}`.
    public const string ManifestFileName = "baseline.json";

    private static readonly XNamespace TeamTest = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    // Reads every `*.trx` under `directory`; null when there is no directory or no TRX file in it — "nothing was
    // recorded" is a different answer from "everything passed", and the report has to tell them apart (§6b).
    public static AutopilotTrxResults? Load(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var files = Directory.GetFiles(directory, "*.trx", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            return null;
        }

        var total = 0;
        var passed = 0;
        var failed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var result in XDocument.Load(file).Descendants(TeamTest + "UnitTestResult"))
            {
                total++;
                var outcome = (string?)result.Attribute("outcome");
                if (string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase))
                {
                    passed++;
                }
                else if (string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add((string?)result.Attribute("testName") ?? string.Empty);
                }
            }
        }

        return new AutopilotTrxResults(files.Length, total, passed, failed, _ManifestSha(Path.Combine(directory, ManifestFileName)));
    }

    // An unreadable or sha-less manifest reads as "no sha recorded", which the report treats as a missing baseline.
    private static string? _ManifestSha(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return manifest.RootElement.TryGetProperty("sha", out var sha) && sha.GetString() is { Length: > 0 } value ? value.Trim() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
