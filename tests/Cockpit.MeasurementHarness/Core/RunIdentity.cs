using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cockpit.MeasurementHarness.Core;

/// <summary>
/// E4: what a report has to be able to say about itself. Two runs that differed only in one flag wrote the
/// same file with the same header, and the original evidence for that measurement is gone (AC-1177).
/// </summary>
public sealed class RunIdentity
{
    private RunIdentity(string scenario, string argv, DateTimeOffset startedUtc, string sha, IReadOnlyDictionary<string, string> flags)
    {
        Scenario = scenario;
        Argv = argv;
        StartedUtc = startedUtc;
        CockpitSha = sha;
        Flags = flags;
    }

    public string Scenario { get; }

    /// <summary>The full received argv, serialised. Not a summary of it, and not the parsed subset.</summary>
    public string Argv { get; }

    public DateTimeOffset StartedUtc { get; }

    public string CockpitSha { get; }

    /// <summary>Every effective flag, including the ones that are off — an absent flag is a setting too.</summary>
    public IReadOnlyDictionary<string, string> Flags { get; }

    /// <summary>The report's name, derived from the full argv so two different runs cannot collide.</summary>
    public string ReportFileName => $"report-{Scenario}-argv-{_ArgvHash()}.txt";

    /// <summary>
    /// Captures the identity of this run. The checkout's SHA is required rather than defaulted: a report
    /// that cannot name the code it measured is not evidence, and guessing it would be worse than refusing.
    /// </summary>
    public static RunIdentity Capture(string scenario, string[] args, IReadOnlyDictionary<string, string> flags, Func<string?>? shaSource = null)
    {
        var sha = (shaSource ?? (() => Environment.GetEnvironmentVariable("COCKPIT_GIT_SHA")))();
        if (string.IsNullOrWhiteSpace(sha))
        {
            throw new InvalidOperationException(
                "COCKPIT_GIT_SHA is required so a report cannot claim an unknown checkout. "
                + "Set it to the SHA of the Cockpit checkout that was built, e.g. $(git rev-parse HEAD).");
        }

        return new RunIdentity(scenario, JsonSerializer.Serialize(args), DateTimeOffset.UtcNow, sha.Trim(), flags);
    }

    /// <summary>The header every report opens with, so the run is identifiable from the file alone.</summary>
    public IEnumerable<string> HeaderLines()
    {
        yield return $"=== run | scenario={Scenario} utc={StartedUtc:O} cockpit-sha={CockpitSha} ===";
        yield return $"argv: {Argv}";
        yield return "effective flags (including the ones that are off):";
        foreach (var (name, value) in Flags.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            yield return $"  {name} = {value}";
        }
    }

    /// <summary>
    /// Writes the report, refusing rather than overwriting. Returns the path written; throws
    /// <see cref="ReportCollisionException"/> when a report with this identity already exists.
    /// </summary>
    public string WriteReport(string directory, string text)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ReportFileName);
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(file, Encoding.UTF8);
            writer.Write(text);
        }
        catch (IOException) when (File.Exists(path))
        {
            throw new ReportCollisionException(path);
        }

        return path;
    }

    private string _ArgvHash() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Argv)))[..16].ToLowerInvariant();
}

/// <summary>Raised when a report with this run's identity already exists. Never overwrite evidence.</summary>
public sealed class ReportCollisionException(string path)
    : IOException($"REFUSED: a report for this exact argv already exists: {path}")
{
    public string Path { get; } = path;
}
