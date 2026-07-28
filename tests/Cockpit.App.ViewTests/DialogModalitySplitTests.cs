using System.Text.RegularExpressions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-367 asks for the split between the two kinds of dialog to be written down and kept. This is where it is
/// kept: a <b>surface</b> is worked in for minutes and opens beside the cockpit, a <b>question</b> is answered
/// in seconds and stays modal.
/// <para>
/// Read off the source rather than exercised, because <c>SessionDialogService</c> cannot be run here at all —
/// every method returns early without an <c>IClassicDesktopStyleApplicationLifetime</c>, and the headless
/// harness has none. <see cref="SurfaceWindowsTests"/> proves what the surface mechanism does; this proves
/// which dialogs were put through it. Same idiom as the theme's own selector guard.
/// </para>
/// </summary>
public sealed partial class DialogModalitySplitTests
{
    // Worked in for minutes, so a modal takes every running session down with it.
    private static readonly string[] SessionSurfaces =
    [
        "ShowNewSessionDialogAsync",
        "ShowProjectsDialogAsync",
        "ShowProjectDialogAsync",
        "ShowManageProfilesAsync",
        "ShowMcpServersDialogAsync",
        "ShowVerifyRunnersDialogAsync",
        "ShowPluginStoreDialogAsync",
        "ShowOptionsDialogAsync",
        "ShowDelegatedTasksDialogAsync",
        "ShowWorktreesDialogAsync",
        "ShowAboutDialogAsync",
    ];

    // Answered in seconds, and nothing may carry on half-answered: a removal to confirm, a password, trusting a
    // plugin, or a picker whose chosen command deliberately runs after it has closed.
    private static readonly string[] SessionQuestions =
    [
        "_CloneIntoProjectAsync",
        "ShowCloneFromGitUrlAsync",
        "ShowScheduleResumeDialogAsync",
        "ShowPluginConsentAsync",
        "ShowCommandPaletteDialogAsync",
        "ShowConfirmationDialogAsync",
        "ShowSetStatusDialogAsync",
    ];

    [Fact]
    public void SessionDialogService_OpensEverySurfaceBesideTheCockpit()
    {
        var members = _Members(_Source("src", "Cockpit.App", "Services", "SessionDialogService.cs"));

        foreach (var name in SessionSurfaces)
        {
            var body = _Body(members, name);
            Assert.True(OpensSurface().IsMatch(body), $"{name} should open its window as a surface");
            Assert.False(OpensModal().IsMatch(body), $"{name} should not open its window as a modal");
        }
    }

    [Fact]
    public void SessionDialogService_KeepsEveryQuestionModal()
    {
        var members = _Members(_Source("src", "Cockpit.App", "Services", "SessionDialogService.cs"));

        foreach (var name in SessionQuestions)
        {
            var body = _Body(members, name);
            Assert.True(OpensModal().IsMatch(body), $"{name} should stay a modal question");
            Assert.False(OpensSurface().IsMatch(body), $"{name} should not open its window as a surface");
        }
    }

    [Fact]
    public void PluginDialogHost_OpensPluginWindowsBesideTheCockpit()
    {
        var members = _Members(_Source("src", "Cockpit.App", "Plugins", "PluginDialogHost.cs"));

        foreach (var name in new[] { "ShowDialogAsync", "ShowSettingsDialogAsync" })
        {
            var body = _Body(members, name);
            Assert.True(OpensSurface().IsMatch(body), $"{name} should open the plugin's window as a surface");
            Assert.False(OpensModal().IsMatch(body), $"{name} should not open the plugin's window as a modal");
        }
    }

    // A name that no longer exists would make every assertion above vacuous, so a rename fails here rather than
    // quietly emptying the guard.
    private static string _Body(IReadOnlyDictionary<string, string> members, string name)
    {
        Assert.True(members.ContainsKey(name), $"{name} was not found — the split it belongs to is no longer guarded");

        return members[name];
    }

    /// <summary>Every member of the one class in the file, by name, with the text up to the next member.</summary>
    private static Dictionary<string, string> _Members(string source)
    {
        var starts = MemberDeclaration().Matches(source);
        var members = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < starts.Count; index++)
        {
            var start = starts[index];
            var end = index + 1 < starts.Count ? starts[index + 1].Index : source.Length;
            members[start.Groups["name"].Value] = source[start.Index..end];
        }

        return members;
    }

    private static string _Source(params string[] relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relative]);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(relative)} above {AppContext.BaseDirectory}");
    }

    /// <summary>A member of the class: declared at one level of indentation, named right before its parameters.</summary>
    [GeneratedRegex(@"^    (?:public|private|internal|protected).*?(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.Multiline)]
    private static partial Regex MemberDeclaration();

    [GeneratedRegex(@"\.ShowDialog[(<]")]
    private static partial Regex OpensModal();

    [GeneratedRegex(@"_ShowSurfaceAsync\(|surfaces\.ShowAsync[(<]")]
    private static partial Regex OpensSurface();
}
