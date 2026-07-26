using FluentAssertions;

namespace Cockpit.Core.Tests.Services;

/// <summary>
/// A web address reaches the shell through <c>ExternalLink</c> and nowhere else (AC-315). Four views had each grown
/// their own copy of the same guard, every copy's comment pointing at the last, and a guard duplicated per view is one
/// that only holds until someone tightens a single copy.
/// <para>
/// Reading the source rather than the compiled app is deliberate, the way <c>PluginVersionSingleSourceTests</c> does
/// it: what this guards against is written in C#, one view at a time, and a private handler in a view is not reachable
/// by reflection.
/// </para>
/// </summary>
public class ExternalLinkSingleSourceTests
{
    /// <summary>
    /// Every file allowed to hand something to the OS handler itself, and <em>how many times</em>. The count is the
    /// point: allowing a file outright would let a second, unguarded shell-out hide in a file that is on the list for
    /// an unrelated one — and <c>CockpitViewModel</c> is on it exactly that way, for revealing a folder in the file
    /// manager. Neither folder caller opens a web address, which <c>ExternalLink</c>'s http(s)-only guard would refuse
    /// by design.
    /// </summary>
    private static readonly Dictionary<string, (int Occurrences, string Reason)> AllowedShellCallers =
        new(StringComparer.Ordinal)
        {
            ["ExternalLink.cs"] = (1, "the one opener; this is where the rule lives"),
            ["CockpitViewModel.cs"] = (1, "reveals a project's folder in the file manager, not a web address"),
            ["WorktreesDialog.axaml.cs"] = (1, "reveals a worktree's folder in the file manager, not a web address"),
        };

    [Fact]
    public void OnlyExternalLink_HandsAWebAddressToTheShell()
    {
        var appSources = _AppSourceFiles().ToList();
        appSources.Should().HaveCountGreaterThan(50,
            "the app has well over fifty source files — finding almost none means the walk broke, not that the rule holds");

        var shellOuts = appSources
            .Select(path => (Name: Path.GetFileName(path), Count: _ShellExecuteCount(path)))
            .Where(file => file.Count > 0)
            .ToList();

        shellOuts.Should().Contain(file => file.Name == "ExternalLink.cs",
            "if the one opener stopped shelling out, this test would pass for the wrong reason");

        var unexpected = shellOuts
            .Where(file => !AllowedShellCallers.TryGetValue(file.Name, out var allowed) || allowed.Occurrences != file.Count)
            .Select(file => $"{file.Name} ({file.Count}×)")
            .ToList();

        unexpected.Should().BeEmpty(
            "a shell-out for a link belongs in ExternalLink.TryOpen — that is where the http(s)-only guard and the " +
            "swallowed launch failure live. If a new caller opens a folder rather than a web address, add or raise its " +
            $"entry in {nameof(AllowedShellCallers)} with the reason. Allowed today: " +
            $"{string.Join(", ", AllowedShellCallers.Select(entry => $"{entry.Key} ({entry.Value.Occurrences}×)"))}");
    }

    private static int _ShellExecuteCount(string path) =>
        File.ReadAllText(path).Split("UseShellExecute = true", StringSplitOptions.None).Length - 1;

    private static IEnumerable<string> _AppSourceFiles()
    {
        var appDirectory = _LocateRepositoryFolder(Path.Combine("src", "Cockpit.App"))
            ?? throw new InvalidOperationException("No src/Cockpit.App directory above the test output — this test reads the repo it belongs to.");

        return Directory.EnumerateFiles(appDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string? _LocateRepositoryFolder(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
