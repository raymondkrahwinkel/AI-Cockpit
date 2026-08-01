using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.TestSupport;
using NSubstitute;
using Path = System.IO.Path;

namespace Cockpit.Plugin.SessionReview.Tests;

/// <summary>
/// The review panel end to end against a real repository in a temp directory: git is actually run, its output is
/// actually parsed, and the tree and the diff pane are actually built. The parser has its own tests; what only a
/// running control can show is whether the <see cref="TreeView"/>'s code-built template produces rows at all —
/// exactly the wiring that compiles perfectly while rendering nothing.
/// </summary>
[Collection("avalonia")]
public class SessionDiffDialogControlTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"cockpit-review-{Guid.NewGuid():n}");

    public SessionDiffDialogControlTests()
    {
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        _Git("init", "-b", "main");
        _Git("config", "user.email", "test@example.com");
        _Git("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, "src", "Alpha.cs"), "one\ntwo\nthree\n");
        _Git("add", "-A");
        _Git("commit", "-m", "first");

        // What the panel has to show: an edit to a tracked file, and a file that was never staged.
        File.WriteAllText(Path.Combine(_repo, "src", "Alpha.cs"), "one\nTWO\nthree\n");
        File.WriteAllText(Path.Combine(_repo, "src", "Untracked.cs"), "brand new\n");
    }

    public void Dispose()
    {
        TestGitDirectory.Remove(_repo);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ThePanel_ShowsBothChangedFilesInItsTree()
    {
        using var panel = _Open();

        var files = panel.TreeFiles();

        Assert.Equal(["src/Alpha.cs", "src/Untracked.cs"], files.Select(f => f.Path).Order());
    }

    [Fact]
    public void ThePanel_FindsTheUntrackedFileGitDiffAloneWouldHaveMissed()
    {
        using var panel = _Open();

        var untracked = Assert.Single(panel.TreeFiles(), f => f.Path == "src/Untracked.cs");

        Assert.Equal(FileChangeKind.Added, untracked.Kind);
        Assert.Equal(1, untracked.Added);
    }

    [Fact]
    public void TheTree_CollapsesTheFolderAndRendersARowPerFile()
    {
        using var panel = _Open();

        // One folder node holding both files, and the code-built item template actually drew their names.
        Assert.Equal(["src"], panel.TreeNodes().Select(n => n.Label));
        var drawn = panel.Texts();
        Assert.Contains("Alpha.cs", drawn);
        Assert.Contains("Untracked.cs", drawn);
        Assert.Contains("src", drawn);
    }

    [Fact]
    public void TheDiffPane_OpensOnTheFirstFileWithItsLinesAndNumbers()
    {
        using var panel = _Open();

        var drawn = panel.Texts();

        Assert.Contains("− two", drawn);      // the removed line, with the panel's own sign column
        Assert.Contains("+ TWO", drawn);      // and the line that replaced it
        Assert.Contains("2", drawn);          // the gutter number they sit on
        Assert.Contains(drawn, t => t.StartsWith("@@", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDiffPane_HighlightsOnlyThePartOfAReplacedLineThatDiffers()
    {
        // "two" became "TWO": the whole line is not the change, the three letters are. Both halves of the pair get
        // their own highlight, and nothing else on screen does.
        using var panel = _Open();

        Assert.Equal(["two", "TWO"], panel.HighlightedRuns());
    }

    [Fact]
    public void SelectingAnotherFile_ReplacesWhatTheDiffPaneShows()
    {
        using var panel = _Open();

        panel.Select("src/Untracked.cs");

        var drawn = panel.Texts();
        Assert.Contains("+ brand new", drawn);
        Assert.DoesNotContain("− two", drawn);
    }

    [Fact]
    public void ARepoWithNoChanges_SaysSoRatherThanDrawingAnEmptyTree()
    {
        _Git("add", "-A");
        _Git("commit", "-m", "second");

        using var panel = _Open();

        Assert.Contains(panel.Texts(), t => t.Contains("No uncommitted changes", StringComparison.Ordinal));
    }

    private _Panel _Open()
    {
        // Build on the UI thread, then wait from this one. Waiting inside the Invoke would hold the very thread the
        // control's own load continuation needs, and the panel would never finish reading — a 30-second deadlock.
        var panel = HeadlessAvalonia.Run(() => _Panel.Attach(_repo));
        panel.WaitUntilLoaded();
        return panel;
    }

    private void _Git(params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();
    }

    /// <summary>A shown window holding the panel, with the reads the tests make of it.</summary>
    private sealed class _Panel(Window window, SessionDiffDialogControl control) : IDisposable
    {
        public static _Panel Attach(string repository)
        {
            var session = Substitute.For<IPluginSessionContext>();
            session.WorkingDirectory.Returns(repository);

            var control = new SessionDiffDialogControl(Substitute.For<ICockpitHost>(), session);
            var window = new Window { Width = 1100, Height = 720, Content = control };
            window.Show();
            return new _Panel(window, control);
        }

        public IReadOnlyList<TreeNode> TreeNodes() => HeadlessAvalonia.Run(
            () => (control.GetLogicalDescendants().OfType<TreeView>().Single().ItemsSource as IEnumerable<TreeNode>)?.ToList() ?? []);

        public IReadOnlyList<FileDiff> TreeFiles() => [.. _Flatten(TreeNodes()).Where(n => n.File is not null).Select(n => n.File!)];

        /// <summary>
        /// Every string the panel actually put on screen — the only honest evidence that it rendered. A line the
        /// panel highlighted word-by-word carries its text in <c>Inlines</c> and leaves <c>Text</c> empty, so both
        /// have to be read or exactly the interesting lines go missing.
        /// </summary>
        public IReadOnlyList<string> Texts() => HeadlessAvalonia.Run(
            () => control.GetLogicalDescendants().OfType<TextBlock>().Select(_TextOf).Where(t => t.Length > 0).ToList());

        /// <summary>The text of every run the panel gave its own background — what the word-level highlight covers.</summary>
        public IReadOnlyList<string> HighlightedRuns() => HeadlessAvalonia.Run(
            () => control.GetLogicalDescendants().OfType<TextBlock>()
                .SelectMany(t => t.Inlines?.OfType<Run>() ?? [])
                .Where(r => r.Background is not null)
                .Select(r => r.Text ?? string.Empty)
                .ToList());

        private static string _TextOf(TextBlock block) =>
            string.IsNullOrEmpty(block.Text)
                ? string.Concat((block.Inlines ?? []).OfType<Run>().Select(r => r.Text))
                : block.Text;

        public void Select(string path) => HeadlessAvalonia.Run(() =>
        {
            var tree = control.GetLogicalDescendants().OfType<TreeView>().Single();
            tree.SelectedItem = _Flatten((IEnumerable<TreeNode>)tree.ItemsSource!).Single(n => n.File?.Path == path);
            Dispatcher.UIThread.RunJobs();
        });

        public void Dispose() => HeadlessAvalonia.Run(window.Close);

        private static IEnumerable<TreeNode> _Flatten(IEnumerable<TreeNode> nodes) =>
            nodes.SelectMany(n => new[] { n }.Concat(_Flatten(n.Children)));

        /// <summary>
        /// The control loads on a task started from its constructor, so the tests have to wait for git rather than
        /// assume it. Polls in short slices, from the calling thread, so the UI thread stays free to run the load.
        /// </summary>
        public void WaitUntilLoaded()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var texts = Texts();
                if (texts.Any(t => t.Contains("uncommitted changes", StringComparison.OrdinalIgnoreCase)))
                {
                    HeadlessAvalonia.Run(() => Dispatcher.UIThread.RunJobs());
                    return;
                }

                Thread.Sleep(50);
            }

            throw new TimeoutException("The review panel never finished reading the repository.");
        }
    }
}
