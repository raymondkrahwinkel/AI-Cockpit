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
        // a name qualified by its namespace (`Core.Sessions.SessionError`) still is. AC-1441: neither is a declaration
        // that shares one: an enum member alone on its line (`Question,`) or a property (`Question { get; }`).
        var forbiddenReference = new Regex(
            $@"(?:(?<!\.)|(?<=Sessions\.))\b(?:{string.Join("|", forbiddenNames.Select(Regex.Escape))})\b(?!(?<=^[ \t]*\w+)[ \t]*(?:[,}}\r\n]|=(?!>)))(?!\s*\{{\s*get)|\bSessionHost\b",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var commentLine = new Regex(@"^\s*//.*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        // AC-1441: a fully qualified name reaches into Infrastructure as surely as a using does.
        var infrastructureReference = new Regex(@"\bCockpit\.Infrastructure\b", RegexOptions.CultureInvariant);
        var violations = new[] { "ViewModels", "Views" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(appRoot, directory),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(path =>
            {
                var code = commentLine.Replace(File.ReadAllText(path), string.Empty);
                return infrastructureReference.IsMatch(code) || forbiddenReference.IsMatch(code);
            })
            .Select(path => Path.GetRelativePath(appRoot, path).Replace('\\', '/'))
            .Order()
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Frontend boundary violation: {string.Join(", ", violations)}");
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
