using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using Xunit.Abstractions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// The issue dialog's wiring, measured in a real (if screenless) Avalonia: the selection the operator is reading,
/// the result line an action leaves behind, the state of the toolbar, and how the description and the prompt preview
/// divide the panel between them. All of it was written untested, and all of it was broken.
/// <para>
/// The dialog fetches its own issues over HTTP and there is no seam to hand it a list, so the loaded set is planted
/// in <c>_all</c> and every rebuild is driven the way the operator drives it — by typing in the search box, which is
/// the same <c>_ApplyFilter</c> path a refresh takes.
/// </para>
/// <para>
/// Every assertion here reads a value out first and asserts on that value: an assertion written as
/// <c>maybeNull?.Field.Should()...</c> is skipped in full when the value is null, which is precisely the state the
/// defect produces — the test then passes on the broken build it was written to catch.
/// </para>
/// </summary>
[Collection("avalonia")]
public class YouTrackDialogControlTests
{
    private static readonly YouTrackIssue First = new("1-1", "AT-1", "Faster startup", "Cold start takes 4s.", "AT", "Backlog");
    private static readonly YouTrackIssue Second = new("1-2", "AT-2", "Fix the sidebar", "It collapses.", "AT", "Backlog");

    private readonly ITestOutputHelper _out;

    public YouTrackDialogControlTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TypingInTheSearchBox_KeepsTheIssueTheOperatorWasReading() => HeadlessAvalonia.Run(() =>
    {
        var harness = DialogHarness.Open(First, Second);

        harness.Select(First);
        harness.Type("-");

        var selectedId = (harness.Grid.SelectedItem as YouTrackIssue)?.IdReadable;
        _out.WriteLine($"selected after filter: {selectedId ?? "<null>"}");
        harness.Close();

        selectedId.Should().Be(First.IdReadable, "a keystroke in the filter is not a request to lose your place");
    });

    [Fact]
    public void AResultOnTheSelectedIssue_SurvivesTheRebuildItTriggers() => HeadlessAvalonia.Run(() =>
    {
        // Start work and Set state both report their outcome and then reload the grid, which rebuilds ItemsSource and
        // re-selects the same issue. Add to prompt reports the same way and needs no YouTrack behind it, so it stands
        // in for them here: what is under test is that a rebuild-plus-restore does not wipe the line (AC-299).
        var harness = DialogHarness.Open(First, Second);
        harness.Host.FakeActions.HasActiveSession = true;
        harness.Select(First);
        harness.Click("Add to prompt");

        var reported = harness.DetailMessage();
        harness.Type("-");
        var afterRebuild = harness.DetailMessage();

        _out.WriteLine($"reported={reported ?? "<none>"} afterRebuild={afterRebuild ?? "<none>"}");
        harness.Close();

        reported.Should().Contain("AT-1", "the action has to report something for this to mean anything");
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

        afterMove.Should().BeNullOrEmpty("what happened to AT-1 says nothing about AT-2");
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
        IssueLinked? linked = null;
        harness.Links.Linked += (_, args) => linked = args;

        harness.Select(First);
        harness.Click("New session");
        harness.Host.OnSessionStarted?.Invoke("pane-1");

        var linkedIssue = linked?.Link.Issue.IdReadable;
        var directory = linked?.WorkingDirectory;
        _out.WriteLine($"linked={linkedIssue ?? "<none>"} directory={directory ?? "<null>"}");
        harness.Close();

        linkedIssue.Should().Be(First.IdReadable);
        directory.Should().Be("/home/operator/repo",
            "a flow that cuts a branch when a ticket is picked needs the path, and an empty one sends it nowhere");
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
    public void NewSession_NamesTheProjectTheSelectedIssueLivesIn_SoTheDialogCanPreselectIt() => HeadlessAvalonia.Run(() =>
    {
        // AC-419: the dialog already knows which issue it is for, so it can say which cockpit project that issue's
        // YouTrack project is linked to (AC-317) instead of leaving the operator on "No project" every time.
        //
        // The issue under test is moved to a project the other fixture is not in. Both fixtures are in AT, so an
        // assertion on AT would hold just as well on a build that read the first row, or the grid's project filter,
        // instead of the selection — the test would pass while proving none of what its name says.
        var elsewhere = Second with { Project = "OTHER" };
        var harness = DialogHarness.Open(First, elsewhere);
        harness.Select(elsewhere);

        harness.Click("New session");

        var link = harness.Host.LastNewSessionPrefill?.LinkedProject;
        var fieldKey = link?.FieldKey;
        var value = link?.Value;
        _out.WriteLine($"link={fieldKey ?? "<null>"}={value ?? "<null>"}");
        harness.Close();

        fieldKey.Should().Be("youtrack.project", "that is the key the project editor stores the link under");
        value.Should().Be("OTHER", "the issue being started decides — not the first row, and not the project filter");
    });

    [Fact]
    public void AnIssueWithNoProject_NamesNoProjectRatherThanAnEmptyOne() => HeadlessAvalonia.Run(() =>
    {
        // An "All projects" query whose response carries no project.shortName leaves the issue with no project at
        // all (YouTrackClient._ExtractProject falls back to the query's tag, and that query has none). A link
        // carrying an empty value would be a question with no answer in it.
        var orphan = First with { Project = string.Empty };
        var harness = DialogHarness.Open(orphan, Second);
        harness.Select(orphan);

        harness.Click("New session");

        var link = harness.Host.LastNewSessionPrefill?.LinkedProject;
        _out.WriteLine($"link={link?.Value ?? "<null>"}");
        harness.Close();

        link.Should().BeNull();
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
        // The url is built from the instance address the operator typed into the settings, so it is not necessarily
        // a web address at all. Anything but http(s) is reported instead of being handed to the shell — this one
        // would otherwise be started with UseShellExecute, which is whatever the desktop has registered for it.
        var harness = DialogHarness.Open(new YouTrackInstance("Odd", "ftp://tracker.example/", string.Empty, string.Empty), First, Second);
        harness.Select(First);

        harness.Press(harness.OpenLink());

        var reported = harness.DetailMessage();
        _out.WriteLine($"reported={reported ?? "<none>"}");
        harness.Close();

        reported.Should().Contain("ftp://tracker.example/issue/AT-1",
            "an operator who clicks a link and sees nothing happen has no way to tell that from a slow browser");
    });

    [Fact]
    public void AnExpandedPromptPreview_LeavesTheDescriptionItsRoom_AndScrollsItself() => HeadlessAvalonia.Run(() =>
    {
        // The template embeds the description verbatim, so the preview is always taller than the description it
        // repeats. Docked at the bottom of the panel and unscrolled, it took every pixel and left the description
        // none — on any issue with more than a screenful of text, which is most of them.
        var longIssue = First with { Description = string.Join("\n", Enumerable.Range(1, 200).Select(line => $"Line {line} of the description.")) };
        var harness = DialogHarness.Open(longIssue, Second);

        harness.Select(longIssue);
        harness.Click("Prompt preview");
        harness.Layout();

        var description = harness.DescriptionScroll();
        var preview = harness.PromptScroll();
        var descriptionHeight = description?.Bounds.Height;
        var previewViewport = preview?.Viewport.Height;
        var previewExtent = preview?.Extent.Height;
        _out.WriteLine($"description height={descriptionHeight:0.#} viewport={previewViewport:0.#} extent={previewExtent:0.#}");
        harness.Close();

        descriptionHeight.Should().BeGreaterThan(200, "the description is the panel's main content, not what is left over");
        previewViewport.Should().BeGreaterThan(0, "the preview has to scroll inside its own box rather than grow past it");
        (previewExtent - previewViewport).Should().BeGreaterThan(0, "a preview taller than its box without a scroller is a clipped preview");
    });

    [Fact]
    public void AHostWithoutTheMarkdownSeam_LeavesNothingOfThePreviousIssueOnScreen() => HeadlessAvalonia.Run(() =>
    {
        // A cockpit older than this plugin's minHostVersion loads it anyway (the gate only bites from host 1.0) and
        // then has no CreateMarkdownView. That exception used to escape _ShowDetail between the title and the prompt,
        // so the panel showed AT-2's heading over AT-1's description — and Add to prompt sent AT-1 (AC-304).
        var harness = DialogHarness.Open(First, Second);
        harness.Select(First);
        harness.Click("Prompt preview");

        harness.Host.MarkdownFailure = new MissingMethodException("ICockpitHost", "CreateMarkdownView");
        harness.Select(Second);

        var description = harness.DescriptionText();
        var preview = harness.PromptPreviewText();
        _out.WriteLine($"description={description} preview starts={preview?[..Math.Min(40, preview.Length)]}");
        harness.Close();

        description.Should().Contain(Second.Description, "the operator selected AT-2, so this is AT-2's panel");
        description.Should().NotContain(First.Description);
        preview.Should().Contain(Second.IdReadable, "the button beside this preview injects it into an agent");
    });

    [Fact]
    public void ABodyThatFailsToRender_OffersNoPromptForAnIssueItIsNotShowing() => HeadlessAvalonia.Run(() =>
    {
        // The fallback above covers the one failure that was found; this covers the shape of the defect. The panel
        // used to swap its heading before building the body, so anything that threw in between left AT-2's title
        // over AT-1's description — and left AT-1's prompt loaded while the grid had moved to AT-2, so "Add to
        // prompt" would inject an issue the operator was no longer looking at. The panel empties instead.
        var harness = DialogHarness.Open(First, Second);
        harness.Select(First);
        harness.Click("Prompt preview");

        harness.Host.MarkdownFailure = new InvalidOperationException("rendering failed for some other reason");
        var selectSecond = () => harness.Select(Second);

        selectSecond.Should().Throw<InvalidOperationException>("an unknown failure is not this dialog's to swallow");
        var showsAnIssue = harness.DetailIsVisible();
        var preview = harness.PromptPreviewText();
        _out.WriteLine($"detail visible={showsAnIssue} preview={preview ?? "<null>"}");
        harness.Close();

        showsAnIssue.Should().BeFalse("a panel that could not be built for AT-2 must not keep standing as AT-1");
        preview.Should().BeNullOrEmpty("whatever is in the preview is what Add to prompt injects");
    });

    /// <summary>
    /// One dialog under test, in a window its real size, with the loaded issue set planted and the fakes it talks to
    /// kept to hand.
    /// </summary>
    private sealed class DialogHarness
    {
        private static readonly YouTrackInstance LocalInstance = new("Local", "http://127.0.0.1:9/", string.Empty, string.Empty);

        private DialogHarness(Window window, YouTrackDialogControl dialog, FakeCockpitHost host, SessionIssueLinks links)
        {
            _window = window;
            _dialog = dialog;
            Host = host;
            Links = links;
        }

        private readonly Window _window;
        private readonly YouTrackDialogControl _dialog;

        public FakeCockpitHost Host { get; }

        public SessionIssueLinks Links { get; }

        public FakeSessionObserver Observer => Host.Observer;

        public DataGrid Grid => _window.GetVisualDescendants().OfType<DataGrid>().First();

        public static DialogHarness Open(params YouTrackIssue[] issues) => Open(LocalInstance, issues);

        public static DialogHarness Open(YouTrackInstance instance, params YouTrackIssue[] issues)
        {
            // A configured instance with a token left blank: the dialog has an instance selected (which "New session"
            // and the issue url both need) but its own load short-circuits before any call goes out.
            var storage = new InMemoryPluginStorage();
            var settings = new YouTrackSettings(storage) { Instances = [instance] };
            var host = new FakeCockpitHost();
            var links = new SessionIssueLinks(host);
            var dialog = new YouTrackDialogControl(settings, host, links, new IssueStateChanges());

            var window = new Window { Width = 1280, Height = 860, Content = dialog };
            window.Show();
            window.UpdateLayout();

            var harness = new DialogHarness(window, dialog, host, links);
            harness._PlantLoadedIssues(issues);
            harness.Type("AT");
            return harness;
        }

        public void Select(YouTrackIssue issue)
        {
            Grid.SelectedItem = Grid.ItemsSource?.OfType<YouTrackIssue>().First(candidate => candidate.IdReadable == issue.IdReadable);
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
                .First(box => box.PlaceholderText?.StartsWith("Filter by id", StringComparison.Ordinal) == true)
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

        /// <summary>The heading of whichever issue the detail panel is currently about.</summary>
        public string? DetailTitle() => _window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Name == "detailTitle")?.Text;

        /// <summary>
        /// Whether the panel is showing an issue at all, as opposed to its "select an issue" placeholder. Asks the
        /// heading whether it is effectively visible: hiding the panel leaves its children's own IsVisible untouched,
        /// so only the answer that walks the parent chain distinguishes the two states.
        /// </summary>
        public bool DetailIsVisible() => _window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Name == "detailTitle")?.IsEffectivelyVisible == true;

        public ScrollViewer? DescriptionScroll() => _Scroller("descriptionScroll");

        public ScrollViewer? PromptScroll() => _Scroller("promptScroll");

        /// <summary>Whatever the description panel is currently showing, however the host chose to render it.</summary>
        public string? DescriptionText() => _TextIn(DescriptionScroll());

        public string? PromptPreviewText() => _TextIn(PromptScroll());

        public void Close() => _window.Close();

        private ScrollViewer? _Scroller(string name) => _window.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(scroller => scroller.Name == name);

        // Both the rendered and the plain-text rendering end up in SelectableTextBlocks, the first as inline runs and
        // the second as Text, so read whichever the control carries rather than assuming one shape.
        private static string? _TextIn(ScrollViewer? scroller) => scroller is null
            ? null
            : string.Concat(scroller.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Select(text => text.Text ?? string.Concat((text.Inlines ?? []).OfType<Run>().Select(run => run.Text))));

        private void _PlantLoadedIssues(YouTrackIssue[] issues)
        {
            var loaded = typeof(YouTrackDialogControl).GetField("_all", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("YouTrackDialogControl no longer keeps its loaded issues in _all.");
            loaded.SetValue(_dialog, issues);
        }
    }
}
