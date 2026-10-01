using System.Text.RegularExpressions;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests.Architecture;

public sealed class FrontendBoundaryTests
{
    [Fact]
    public void ViewModelsAndViews_OnlyUseFrontendContracts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appRoot = Path.Combine(repositoryRoot, "src", "Cockpit.App");
        var forbiddenNames = new[]
        {
            "ISessionManager",
            "ISessionRuntime",
            "ISessionDriver",
            "ISessionTurnGate",
            nameof(SessionEvent),
        }
        .Concat(typeof(SessionEvent).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(SessionEvent)))
            .Select(type => type.Name));
        // AC-1449: a member that shares an event's name (`TranscriptEntryKind.TurnCompleted`) is no dependency on it;
        // a name qualified by its namespace (`Core.Sessions.SessionError`) still is.
        var forbiddenReference = new Regex(
            $@"(?:(?<!\.)|(?<=Sessions\.))\b(?:{string.Join("|", forbiddenNames.Select(Regex.Escape))})\b|\bSessionHost\b",
            RegexOptions.CultureInvariant);
        var infrastructureUsing = new Regex(
            @"^\s*using\s+Cockpit\.Infrastructure(?:[.;])",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var violations = new[] { "ViewModels", "Views" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(appRoot, directory),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return infrastructureUsing.IsMatch(source) || forbiddenReference.IsMatch(source);
            })
            .Select(path => Path.GetRelativePath(appRoot, path).Replace('\\', '/'))
            .Order()
            .ToArray();
        var allowlistPath = Path.Combine(
            repositoryRoot,
            "tests",
            "Cockpit.App.ViewTests",
            "Architecture",
            "frontend-boundary-allowlist.txt");
        var allowlist = File.ReadLines(allowlistPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Order()
            .ToArray();
        var newViolations = violations.Except(allowlist).ToArray();
        var staleAllowlistEntries = allowlist.Except(violations).ToArray();

        Assert.True(
            newViolations.Length == 0,
            $"Frontend boundary violation: {string.Join(", ", newViolations)}");
        Assert.True(
            staleAllowlistEntries.Length == 0,
            $"remove {string.Join(", ", staleAllowlistEntries)} from the allowlist");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cockpit.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
