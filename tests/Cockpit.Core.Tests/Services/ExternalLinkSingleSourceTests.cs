using System.Text.RegularExpressions;

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
/// <para>
/// It is a tripwire, not a proof, and worth being honest about what it does not catch: a shell-out whose flag arrives
/// through a variable or a constant rather than as a literal, one set on a later line instead of in the initializer,
/// and one that reaches a browser by some other route entirely (<c>explorer.exe &lt;url&gt;</c>). It scans
/// <c>Cockpit.App</c> only, because that is where the shared opener lives — <c>Cockpit.Infrastructure</c> cannot
/// reference it and so cannot be held to this rule.
/// </para>
/// <para>
/// The enforcement that does not depend on catching a shape lives in <c>ExternalLink</c> itself, which re-checks the
/// scheme on every route into the shell. This test is the second layer: it says "a new one appeared, go and look".
/// </para>
/// </summary>
public partial class ExternalLinkSingleSourceTests
{
    /// <summary>
    /// Every file allowed to hand something to the OS handler itself, and <em>how many times</em>. Two details are the
    /// point. The count, because allowing a file outright would let a second, unguarded shell-out hide in one that is on
    /// the list for an unrelated call — and <c>CockpitViewModel</c> is on it exactly that way, for revealing a folder in
    /// the file manager. And the path rather than the bare file name, so a second file of the same name in another
    /// folder cannot inherit this permission. Neither folder caller opens a web address, which <c>ExternalLink</c>'s
    /// http(s)-only guard would refuse by design.
    /// </summary>
    private static readonly Dictionary<string, (int Occurrences, string Reason)> AllowedShellCallers =
        new(StringComparer.Ordinal)
        {
            ["Services/ExternalLink.cs"] = (1, "the one opener; this is where the rule lives"),
            ["ViewModels/CockpitViewModel.cs"] = (1, "reveals a project's folder in the file manager, not a web address"),
            ["Views/WorktreesDialog.axaml.cs"] = (1, "reveals a worktree's folder in the file manager, not a web address"),
        };

    [Fact]
    public void OnlyExternalLink_HandsAWebAddressToTheShell()
    {
        var appDirectory = _LocateRepositoryFolder(Path.Combine("src", "Cockpit.App"))
            ?? throw new InvalidOperationException("No src/Cockpit.App directory above the test output — this test reads the repo it belongs to.");

        var appSources = _AppSourceFiles(appDirectory).ToList();
        Assert.True(System.Linq.Enumerable.Count(appSources) > 50,
            "the app has well over fifty source files — finding almost none means the walk broke, not that the rule holds");

        var shellOuts = appSources
            .Select(path => (Path: _RelativeToApp(appDirectory, path), Count: _ShellExecuteCount(path)))
            .Where(file => file.Count > 0)
            .ToList();

        Assert.Contains(shellOuts, file => file.Path == "Services/ExternalLink.cs");

        var unexpected = shellOuts
            .Where(file => !AllowedShellCallers.TryGetValue(file.Path, out var allowed) || allowed.Occurrences != file.Count)
            .Select(file => $"{file.Path} ({file.Count}×)")
            .ToList();

        Assert.Empty(unexpected);
    }

    private static int _ShellExecuteCount(string path) => ShellExecuteRegex().Count(File.ReadAllText(path));

    /// <summary>Whitespace-tolerant, so reformatting the assignment does not quietly retire this test.</summary>
    [GeneratedRegex(@"UseShellExecute\s*=\s*true")]
    private static partial Regex ShellExecuteRegex();

    private static IEnumerable<string> _AppSourceFiles(string appDirectory) =>
        Directory.EnumerateFiles(appDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>The file's path inside the app project, with forward slashes so the allowlist reads the same on every OS.</summary>
    private static string _RelativeToApp(string appDirectory, string path) =>
        Path.GetRelativePath(appDirectory, path).Replace(Path.DirectorySeparatorChar, '/');

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
