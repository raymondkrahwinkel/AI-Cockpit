using System.Text.RegularExpressions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Two call-site orderings in <c>SessionDialogService</c> where getting it wrong loses what the operator entered.
/// <para>
/// Read off the source rather than exercised, because <c>SessionDialogService</c> cannot be run here at all —
/// every method returns early without an <c>IClassicDesktopStyleApplicationLifetime</c>, and the headless
/// harness has none. <see cref="SurfaceWindowsTests"/> proves what the surface mechanism does.
/// </para>
/// </summary>
public sealed partial class DialogModalitySplitTests
{
    // A surface that answers something reads its answer when the window closes, and a dialog's own code-behind
    // subscribes to CloseRequested from OnDataContextChanged — so setting the DataContext first puts the handler
    // that closes the window ahead of the one that records the answer, and the answer is read before it is
    // written. It cost a build where Start session and Save both came back as a cancel. There is no type to hang
    // this on, so it is held here, at the two call sites where the order matters.
    // The trailing brace is load-bearing: "new NewSessionDialog" on its own also matches the view model built a
    // few lines earlier, which put the guard in the wrong place — and it caught itself doing it.
    [Theory]
    [InlineData("ShowNewSessionDialogAsync", "new NewSessionDialog {")]
    [InlineData("ShowProjectDialogAsync", "new ProjectDialog {")]
    public void SessionDialogService_RecordsTheAnswerBeforeTheDialogCanClose(string method, string construction)
    {
        var body = _Body(_Members(_Source("src", "Cockpit.App", "Services", "SessionDialogService.cs")), method);

        var subscribes = body.IndexOf("CloseRequested +=", StringComparison.Ordinal);
        var constructs = body.IndexOf(construction, StringComparison.Ordinal);

        Assert.True(subscribes >= 0, $"{method} no longer records the answer through CloseRequested");
        Assert.True(constructs >= 0, $"{method} no longer builds its dialog with {construction}");
        Assert.True(
            subscribes < constructs,
            $"{method} must subscribe to CloseRequested before setting the dialog's DataContext, or the answer is read before it is written");
    }

    // Two projects can be edited side by side; the same project twice is two forms saving over each other. That
    // distinction lives in the key, and the service cannot be exercised here to prove it behaves — it returns
    // early without a desktop lifetime — so the key's shape is held instead. A mutation dropping the id escaped
    // every other test in this repository.
    [Fact]
    public void SessionDialogService_KeysAProjectSurfaceOnTheProjectItEdits()
    {
        var body = _Body(_Members(_Source("src", "Cockpit.App", "Services", "SessionDialogService.cs")), "ShowProjectDialogAsync");

        Assert.Contains("typeof(ProjectDialog)", body, StringComparison.Ordinal);
        Assert.Contains("project?.Id", body, StringComparison.Ordinal);
    }

    // A name that no longer exists would make every assertion above vacuous, so a rename fails here rather than
    // quietly emptying the guard.
    private static string _Body(IReadOnlyDictionary<string, string> members, string name)
    {
        Assert.True(members.ContainsKey(name), $"{name} was not found — the call site it guards is no longer checked");

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
}
