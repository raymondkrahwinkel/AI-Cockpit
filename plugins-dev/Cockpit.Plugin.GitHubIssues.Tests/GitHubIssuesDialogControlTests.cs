using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using Xunit.Abstractions;

namespace Cockpit.Plugin.GitHubIssues.Tests;

/// <summary>
/// The issue dialog's wiring, measured in a real (if screenless) Avalonia — the same suite the YouTrack plugin's
/// dialog carries, against this plugin's own copy of it. The two dialogs are deliberately separate code (plugins
/// are built, versioned and installed on their own), so a fix landing in one proves nothing about the other: this
/// is where the GitHub copy proves it for itself.
/// <para>
/// The dialog fetches its own issues and there is no seam to hand it a list, so the loaded set is planted in
/// <c>_all</c> and every rebuild is driven the way the operator drives it — by typing in the search box, which is
/// the same <c>_ApplyFilter</c> path a refresh takes.
/// </para>
/// <para>
/// Every assertion here reads a value out first and asserts on that value: an assertion written as
/// <c>maybeNull?.Field.Should()...</c> is skipped in full when the value is null, which is precisely the state the
/// defect produces — the test then passes on the broken build it was written to catch.
/// </para>
/// </summary>
[Collection("avalonia")]
public class GitHubIssuesDialogControlTests
{
    private static readonly GitHubIssue First = new(41, "Faster startup", "https://github.com/octocat/hello-world/issues/41", "Cold start takes 4s.", "octocat/hello-world");
    private static readonly GitHubIssue Second = new(42, "Fix the sidebar", "https://github.com/octocat/hello-world/issues/42", "It collapses.", "octocat/hello-world");

    private readonly ITestOutputHelper _out;

    public GitHubIssuesDialogControlTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TypingInTheSearchBox_KeepsTheIssueTheOperatorWasReading() => HeadlessAvalonia.Run(() =>
    {
        var harness = DialogHarness.Open(First, Second);

        harness.Select(First);
        harness.Type("/");

        var selectedNumber = (harness.Grid.SelectedItem as GitHubIssue)?.Number;
        _out.WriteLine($"selected after filter: {selectedNumber?.ToString() ?? "<null>"}");
        harness.Close();

        selectedNumber.Should().Be(First.Number, "a keystroke in the filter is not a request to lose your place");
    });

    [Fact]
    public void AResultOnTheSelectedIssue_SurvivesTheRebuildItTriggers() => HeadlessAvalonia.Run(() =>
    {
        // Refresh and the "Assigned to me" toggle both rebuild ItemsSource and re-select the same issue. Add to
        // prompt reports its outcome the same way those do and needs no GitHub behind it, so it stands in for them
        // here: what is under test is that a rebuild-plus-restore does not wipe the line.
        var harness = DialogHarness.Open(First, Second);
        harness.Host.FakeActions.HasActiveSession = true;
        harness.Select(First);
        harness.Click("Add to prompt");

        var reported = harness.DetailMessage();
        harness.Type("/");
        var afterRebuild = harness.DetailMessage();

        _out.WriteLine($"reported={reported ?? "<none>"} afterRebuild={afterRebuild ?? "<none>"}");
        harness.Close();

        reported.Should().Contain("#41", "the action has to report something for this to mean anything");
        afterRebuild.Should().Be(reported, "the result belongs to the issue, not to the selection event that redrew it");
    });

    [Fact]
    public void MovingToAnotherIssue_ClearsThePreviousResult() => HeadlessAvalonia.Run(() =>
    {
        // The other half of the same rule: keeping the line across a rebuild must not turn into keeping it forever.
        var harness = DialogHarness.Open(First, Second);
        harness.Host.FakeActions.HasActiveSession = true;
        harness.Select(First);
        harness.Click("Add to prompt");

        harness.Select(Second);
        var afterMove = harness.DetailMessage();

        _out.WriteLine($"afterMove={afterMove ?? "<none>"}");
        harness.Close();

        afterMove.Should().BeNullOrEmpty("what happened to #41 says nothing about #42");
    });

    [Fact]
    public void AddToPrompt_WithoutASession_ExplainsItselfWhileDisabled() => HeadlessAvalonia.Run(() =>
    {
        var harness = DialogHarness.Open(First, Second);
        harness.Host.FakeActions.HasActiveSession = false;

        harness.Select(First);
        var inject = harness.Button("Add to prompt");

        var isEnabled = inject.IsEnabled;
        var showsWhileDisabled = ToolTip.GetShowOnDisabled(inject);
        _out.WriteLine($"enabled={isEnabled} tip={ToolTip.GetTip(inject)} showOnDisabled={showsWhileDisabled}");
        harness.Close();

        isEnabled.Should().BeFalse();
        showsWhileDisabled.Should().BeTrue("Avalonia shows no tooltip on a disabled control, so the explanation never reaches the operator without this");
    });

    [Fact]
    public void AStartedSession_MakesAddToPromptUsableAgain() => HeadlessAvalonia.Run(() =>
    {
        var harness = DialogHarness.Open(First, Second);
        harness.Host.FakeActions.HasActiveSession = false;
        harness.Select(First);
        harness.Click("New session");

        // New session created the very thing Add to prompt was missing.
        harness.Host.FakeActions.HasActiveSession = true;
        harness.Host.OnSessionStarted?.Invoke("pane-1");

        var isEnabled = harness.Button("Add to prompt").IsEnabled;
        _out.WriteLine($"enabled after onStarted={isEnabled}");
        harness.Close();

        isEnabled.Should().BeTrue("the session the operator just started is the session Add to prompt injects into");
    });

    [Fact]
    public void AStartedSession_LinksTheIssueToWhereThatSessionWorks() => HeadlessAvalonia.Run(() =>
    {
        var harness = DialogHarness.Open(First, Second);
        harness.Observer.ActiveSessionWorkingDirectory = "/home/operator/repo";
        IssuePicked? picked = null;
        harness.Links.Picked += (_, args) => picked = args;

        harness.Select(First);
        harness.Click("New session");
        harness.Host.OnSessionStarted?.Invoke("pane-1");

        var pickedNumber = picked?.Issue.Number;
        var directory = picked?.WorkingDirectory;
        _out.WriteLine($"picked={pickedNumber?.ToString() ?? "<none>"} directory={directory ?? "<null>"}");
        harness.Close();

        pickedNumber.Should().Be(First.Number);
        directory.Should().Be("/home/operator/repo",
            "a flow that cuts a branch when an issue is picked needs the path, and an empty one sends it nowhere");
    });

    [Fact]
    public void NewSession_GoesInertWhileItsDialogIsOpen() => HeadlessAvalonia.Run(() =>
    {
        // The new-session dialog is modal to the main window, not to this one, so nothing but this button stops a
        // second click from opening a second dialog with its own onStarted — and its own session.
        var harness = DialogHarness.Open(First, Second);
        harness.Select(First);

        harness.Click("New session");

        var opened = harness.Host.NewSessionDialogsOpened;
        var stillArmed = harness.Button("New session").IsEnabled;
        _out.WriteLine($"dialogs opened={opened} button still armed={stillArmed}");
        harness.Close();

        opened.Should().Be(1);
        stillArmed.Should().BeFalse("a second press while the first dialog is up would start a second session");
    });

    [Fact]
    public void ClosingTheNewSessionDialog_ArmsTheButtonAgain() => HeadlessAvalonia.Run(() =>
    {
        var harness = DialogHarness.Open(First, Second);
        harness.Select(First);
        harness.Click("New session");

        harness.Host.CloseNewSessionDialog();

        var isEnabled = harness.Button("New session").IsEnabled;
        _out.WriteLine($"enabled after close={isEnabled}");
        harness.Close();

        isEnabled.Should().BeTrue("guarding against a second dialog must not cost the operator the button for good");
    });

    [Fact]
    public void OpenInBrowser_WithAnAddressItCannotLaunch_SaysSoRatherThanRunningIt() => HeadlessAvalonia.Run(() =>
    {
        // The url comes off the fetched issue, so a mode change or a bad response can put something in it that is
        // not a web address. Anything but http(s) is reported instead of being handed to the shell — and a launch
        // that does not happen has to be said out loud, which this used to swallow entirely.
        var odd = First with { Url = "file:///etc/passwd" };
        var harness = DialogHarness.Open(odd, Second);
        harness.Select(odd);

        harness.Press(harness.OpenLink());

        var reported = harness.DetailMessage();
        _out.WriteLine($"reported={reported ?? "<none>"}");
        harness.Close();

        reported.Should().Contain("file:///etc/passwd",
            "an operator who clicks a link and sees nothing happen has no way to tell that from a slow browser");
    });

    [Fact]
    public void AnExpandedPromptPreview_LeavesTheDescriptionItsRoom_AndScrollsItself() => HeadlessAvalonia.Run(() =>
    {
        // The template embeds the body verbatim, so the preview is always taller than the body it repeats. Docked
        // at the bottom of the panel and unscrolled, it took every pixel and left the body none — on any issue with
        // more than a screenful of text, which is most of them.
        var longIssue = First with { Body = string.Join("\n", Enumerable.Range(1, 200).Select(line => $"Line {line} of the body.")) };
        var harness = DialogHarness.Open(longIssue, Second);

        harness.Select(longIssue);
        harness.Click("Prompt preview");
        harness.Layout();

        var descriptionHeight = harness.DescriptionScroll()?.Bounds.Height;
        var preview = harness.PromptScroll();
        var previewViewport = preview?.Viewport.Height;
        var previewExtent = preview?.Extent.Height;
        _out.WriteLine($"description height={descriptionHeight:0.#} viewport={previewViewport:0.#} extent={previewExtent:0.#}");
        harness.Close();

        descriptionHeight.Should().BeGreaterThan(200, "the body is the panel's main content, not what is left over");
        previewViewport.Should().BeGreaterThan(0, "the preview has to scroll inside its own box rather than grow past it");
        (previewExtent - previewViewport).Should().BeGreaterThan(0, "a preview taller than its box without a scroller is a clipped preview");
    });

    /// <summary>
    /// One dialog under test, in a window its real size, with the loaded issue set planted and the fakes it talks to
    /// kept to hand.
    /// </summary>
    private sealed class DialogHarness
    {
        private DialogHarness(Window window, GitHubIssuesDialogControl dialog, FakeCockpitHost host, SessionIssueLinks links)
        {
            _window = window;
            _dialog = dialog;
            Host = host;
            Links = links;
        }

        private readonly Window _window;
        private readonly GitHubIssuesDialogControl _dialog;

        public FakeCockpitHost Host { get; }

        public SessionIssueLinks Links { get; }

        public FakeSessionObserver Observer => Host.Observer;

        public DataGrid Grid => _window.GetVisualDescendants().OfType<DataGrid>().First();

        public static DialogHarness Open(params GitHubIssue[] issues)
        {
            // Settings on their defaults: the CLI is off and no repository is set, so the dialog's own load
            // short-circuits on "No repository set" before any call goes out — nothing is fetched, and the issue
            // set under test is the one planted below.
            var storage = new InMemoryPluginStorage();
            var settings = new GitHubIssuesSettings(storage);
            var host = new FakeCockpitHost();
            var links = new SessionIssueLinks();
            var dialog = new GitHubIssuesDialogControl(settings, host, links);

            var window = new Window { Width = 1280, Height = 860, Content = dialog };
            window.Show();
            window.UpdateLayout();

            var harness = new DialogHarness(window, dialog, host, links);
            harness._PlantLoadedIssues(issues);
            harness.Type("octocat");
            return harness;
        }

        public void Select(GitHubIssue issue)
        {
            Grid.SelectedItem = Grid.ItemsSource?.OfType<GitHubIssue>().First(candidate => candidate.Number == issue.Number);
            Layout();
        }

        /// <summary>
        /// Types into the filter box the way the operator does. Assigning <c>TextBox.Text</c> would not do: Avalonia
        /// raises <c>TextChanged</c> from its input handling, so a programmatic assignment never reaches the handler
        /// that rebuilds the list — the very path under test.
        /// </summary>
        public void Type(string text)
        {
            _window.GetVisualDescendants().OfType<TextBox>()
                .First(box => box.PlaceholderText?.StartsWith("Filter by title", StringComparison.Ordinal) == true)
                .Focus();
            _window.KeyTextInput(text);
            Layout();
        }

        public Button Button(string label) => _window.GetVisualDescendants().OfType<Button>()
            .First(button => button.Content as string == label
                             || button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == label));

        /// <summary>The icon-only link button beside the title, which carries no text to find it by.</summary>
        public Button OpenLink() => _window.GetVisualDescendants().OfType<Button>()
            .First(button => ToolTip.GetTip(button) as string == "Open in browser");

        public void Click(string label)
        {
            var button = Button(label);
            button.IsEnabled.Should().BeTrue($"\"{label}\" has to be clickable for this test to mean anything");
            Press(button);
        }

        public void Press(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Layout();
        }

        public void Layout() => _window.UpdateLayout();

        /// <summary>The detail panel's own result line.</summary>
        public string? DetailMessage() => _window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Name == "detailStatus")?.Text;

        public ScrollViewer? DescriptionScroll() => _Scroller("descriptionScroll");

        public ScrollViewer? PromptScroll() => _Scroller("promptScroll");

        public void Close() => _window.Close();

        private ScrollViewer? _Scroller(string name) => _window.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(scroller => scroller.Name == name);

        private void _PlantLoadedIssues(GitHubIssue[] issues)
        {
            var loaded = typeof(GitHubIssuesDialogControl).GetField("_all", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("GitHubIssuesDialogControl no longer keeps its loaded issues in _all.");
            loaded.SetValue(_dialog, issues);
        }
    }
}
