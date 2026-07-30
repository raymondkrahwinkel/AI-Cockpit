using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
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

        Assert.Equal(First.Number, selectedNumber);
    });

    [Fact]
    public void ASelectedRow_TintsWithTheCockpitAccent_NotTheSystemOne() => HeadlessAvalonia.Run(() =>
    {
        // AC-423: DataGridRow's Fluent control theme fills its selection Rectangle (Rectangle#BackgroundRectangle,
        // /template/ Fill) with Avalonia's own system accent (#0078d7) at every :selected state — this repo's own
        // Theme.axaml named nothing for that part before this fix, so the row painted a colour no token accounted
        // for (found by the AC-338 palette baseline). CockpitAccentSelectionColor is the theme's own accent tint,
        // already used for the same purpose in a ComboBox's open dropdown.
        var harness = DialogHarness.Open(First, Second);
        harness.Select(First);

        var row = harness.Grid.GetVisualDescendants().OfType<DataGridRow>().First(candidate => Equals(candidate.DataContext, First));
        var rectangle = row.GetVisualDescendants().OfType<Rectangle>().First(candidate => candidate.Name == "BackgroundRectangle");
        var fill = (rectangle.Fill as ISolidColorBrush)?.Color;
        var token = (Color)(Application.Current?.FindResource("CockpitAccentSelectionColor")
            ?? throw new InvalidOperationException("The theme has no CockpitAccentSelectionColor."));

        harness.Close();

        Assert.Equal(token, fill);
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

        Assert.Contains("#41", reported);
        Assert.Equal(reported, afterRebuild);
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

        Assert.True(string.IsNullOrEmpty(afterMove), "what happened to #41 says nothing about #42");
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

        Assert.False(isEnabled);
        Assert.True(showsWhileDisabled, "Avalonia shows no tooltip on a disabled control, so the explanation never reaches the operator without this");
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

        Assert.True(isEnabled, "the session the operator just started is the session Add to prompt injects into");
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

        Assert.Equal(First.Number, pickedNumber);
        Assert.Equal("/home/operator/repo", directory);
    });

    [Fact]
    public void NewSession_NamesTheSessionAfterTheRepositoryTheIssueCameFrom() => HeadlessAvalonia.Run(() =>
    {
        // The sidebar lists sessions by name. "#42" is unique within a repository and nowhere else, and the CLI mode
        // this plugin is built around lists every repo an owner has — so two repos in view meant two rows reading
        // "#42" with nothing on either to say which was which (AC-313).
        var harness = DialogHarness.Open(First, Second);
        harness.Select(Second);

        harness.Click("New session");

        var sessionName = harness.Host.LastPrefill?.SessionName;
        _out.WriteLine($"session name={sessionName ?? "<null>"}");
        harness.Close();

        Assert.Equal("hello-world#42", sessionName);
    });

    [Fact]
    public void AnIssueWhoseRepositoryIsUnknown_IsStillNamedAfterItsNumber() => HeadlessAvalonia.Run(() =>
    {
        // gh can return an issue without the repository field, which used to be the only shape this name had. It
        // stays that shape rather than becoming a name that opens with a stray separator.
        var orphan = First with { Repository = string.Empty };
        var harness = DialogHarness.Open("startup", orphan);
        harness.Select(orphan);

        harness.Click("New session");

        var sessionName = harness.Host.LastPrefill?.SessionName;
        _out.WriteLine($"session name={sessionName ?? "<null>"}");
        harness.Close();

        Assert.Equal("#41", sessionName);
    });

    [Fact]
    public void NewSession_NamesTheRepositoryTheIssueIsOn_SoTheDialogCanPreselectItsProject() => HeadlessAvalonia.Run(() =>
    {
        // AC-419: the dialog already knows which issue it is for, so it can say which cockpit project that issue's
        // repository is linked to (AC-317) instead of leaving the operator on "No project" every time.
        //
        // The issue under test is moved to a second repository. Both fixtures are on octocat/hello-world, so an
        // assertion on that would hold just as well on a build that read the first row, or the repository filter,
        // instead of the selection — the test would pass while proving none of what its name says.
        var elsewhere = Second with { Repository = "octocat/other-repo" };
        var harness = DialogHarness.Open(First, elsewhere);
        harness.Select(elsewhere);

        harness.Click("New session");

        var link = harness.Host.LastPrefill?.LinkedProject;
        var fieldKey = link?.FieldKey;
        var value = link?.Value;
        _out.WriteLine($"link={fieldKey ?? "<null>"}={value ?? "<null>"}");
        harness.Close();

        Assert.Equal("github.repository", fieldKey);
        Assert.Equal("octocat/other-repo", value);
    });

    [Fact]
    public void AnIssueWhoseRepositoryIsUnknown_NamesNoProjectRatherThanAnEmptyOne() => HeadlessAvalonia.Run(() =>
    {
        // The same gh response that costs the name its repository has nothing to look a project up by either. A link
        // carrying an empty value would be a question with no answer in it.
        var orphan = First with { Repository = string.Empty };
        var harness = DialogHarness.Open("startup", orphan);
        harness.Select(orphan);

        harness.Click("New session");

        var link = harness.Host.LastPrefill?.LinkedProject;
        _out.WriteLine($"link={link?.Value ?? "<null>"}");
        harness.Close();

        Assert.Null(link);
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

        Assert.Equal(1, opened);
        Assert.False(stillArmed, "a second press while the first dialog is up would start a second session");
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

        Assert.True(isEnabled, "guarding against a second dialog must not cost the operator the button for good");
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

        Assert.Contains("file:///etc/passwd", reported, StringComparison.Ordinal);
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

        Assert.True(descriptionHeight > 200, "the body is the panel's main content, not what is left over");
        Assert.True(previewViewport > 0, "the preview has to scroll inside its own box rather than grow past it");
        Assert.True(previewExtent - previewViewport > 0, "a preview taller than its box without a scroller is a clipped preview");
    });

    [Fact]
    public void AHostWithoutTheMarkdownSeam_LeavesNothingOfThePreviousIssueOnScreen() => HeadlessAvalonia.Run(() =>
    {
        // A cockpit older than this plugin's minHostVersion loads it anyway (the gate only bites from host 1.0) and
        // then has no CreateMarkdownView. That exception used to escape _ShowDetail between the title and the prompt,
        // so the panel showed #42's heading over #41's body — and Add to prompt sent #41 (AC-304).
        var harness = DialogHarness.Open(First, Second);
        harness.Select(First);
        harness.Click("Prompt preview");

        harness.Host.MarkdownFailure = new MissingMethodException("ICockpitHost", "CreateMarkdownView");
        harness.Select(Second);

        var description = harness.DescriptionText();
        var preview = harness.PromptPreviewText();
        _out.WriteLine($"description={description} preview starts={preview?[..Math.Min(40, preview.Length)]}");
        harness.Close();

        Assert.Contains(Second.Body!, description, StringComparison.Ordinal);
        Assert.DoesNotContain(First.Body!, description);
        Assert.Contains(Second.Number.ToString(), preview, StringComparison.Ordinal);
    });

    [Fact]
    public void ABodyThatFailsToRender_OffersNoPromptForAnIssueItIsNotShowing() => HeadlessAvalonia.Run(() =>
    {
        // The fallback above covers the one failure that was found; this covers the shape of the defect. The panel
        // used to swap its heading before building the body, so anything that threw in between left #42's title
        // over #41's body — and left #41's prompt loaded while the grid had moved to #42, so "Add to prompt" would
        // inject an issue the operator was no longer looking at. The panel empties instead.
        var harness = DialogHarness.Open(First, Second);
        harness.Select(First);
        harness.Click("Prompt preview");

        harness.Host.MarkdownFailure = new InvalidOperationException("rendering failed for some other reason");
        var selectSecond = () => harness.Select(Second);

        Assert.Throws<InvalidOperationException>(selectSecond);
        var showsAnIssue = harness.DetailIsVisible();
        var preview = harness.PromptPreviewText();
        _out.WriteLine($"detail visible={showsAnIssue} preview={preview ?? "<null>"}");
        harness.Close();

        Assert.False(showsAnIssue, "a panel that could not be built for #42 must not keep standing as #41");
        Assert.True(string.IsNullOrEmpty(preview), "whatever is in the preview is what Add to prompt injects");
    });

    [Fact]
    public void RepoFilter_PreselectsTheLinkedProjectsRepository_OnFirstPopulation() => HeadlessAvalonia.Run(() =>
    {
        // AC-317, diagnosed alongside AC-519: the doc comment on _PopulateRepoFilter claims "on the first
        // population there is no selection yet, and that is where the project's own link gets its one chance to
        // be the answer" — but _repoFilter is constructed with SelectedIndex = 0, and a ComboBox resolves that to
        // a real (non-null) SelectedItem synchronously, before any load ever runs (measured the same way AC-519's
        // _labelOptionsPopulated guard was: a throwaway diagnostic, not kept). So "_repoFilter.SelectedItem as
        // string ?? linkedRepository" never reaches the right-hand side — this proves whether that costs the
        // operator the preselection AC-317 promises.
        var repoA = First with { Repository = "acme/foo" };
        var repoB = Second with { Repository = "acme/bar" };
        var harness = DialogHarness.Open("acme", repoA, repoB);
        harness.SetLinkedRepository("acme/foo");

        harness.PopulateRepoFilter("acme/foo", "acme/bar");

        var selected = harness.RepoFilter.SelectedItem as string;
        _out.WriteLine($"selected={selected ?? "<null>"}");
        harness.Close();

        Assert.Equal("acme/foo", selected);
    });

    [Fact]
    public void RepoFilter_WithNoLinkedRepository_FallsBackToAll() => HeadlessAvalonia.Run(() =>
    {
        // The other half of the same fix: an unset link must not somehow end up selecting a repository at random.
        var repoA = First with { Repository = "acme/foo" };
        var repoB = Second with { Repository = "acme/bar" };
        var harness = DialogHarness.Open("acme", repoA, repoB);
        harness.SetLinkedRepository(string.Empty);

        harness.PopulateRepoFilter("acme/foo", "acme/bar");

        var selected = harness.RepoFilter.SelectedItem as string;
        _out.WriteLine($"selected={selected ?? "<null>"}");
        harness.Close();

        Assert.Equal("All", selected);
    });

    [Fact]
    public void RepoFilter_ALinkedRepoWithNoLabelMatchingIssues_StillAppearsAndIsPreselected() => HeadlessAvalonia.Run(() =>
    {
        // Adversarial-review defect: _PopulateRepoFilter used to build its options from _all, which — by the time
        // it runs — has already been narrowed by the server-side label filter (AC-519). A repository linked to the
        // project (AC-317) that simply has no open issue carrying the currently selected label never made it into
        // _all at all, so it could neither be offered in the dropdown nor preselected — silently undoing the very
        // fix RepoFilter_PreselectsTheLinkedProjectsRepository_OnFirstPopulation proves, through a different route,
        // the moment any label filter is picked. The fix sources repo options independently of _all (the way
        // labelOptions already is) — proven here by planting only "acme/bar" in _all (standing in for that
        // label-narrowed fetch) while still handing PopulateRepoFilter the full repository list "acme/foo" (the
        // linked repo) came from separately.
        var onlyBar = Second with { Repository = "acme/bar" };
        var harness = DialogHarness.Open("acme", onlyBar);
        harness.SetLinkedRepository("acme/foo");

        harness.PopulateRepoFilter("acme/foo", "acme/bar");

        var options = harness.RepoFilter.ItemsSource?.OfType<string>().ToList() ?? [];
        var selected = harness.RepoFilter.SelectedItem as string;
        _out.WriteLine($"options=[{string.Join(", ", options)}] selected={selected ?? "<null>"}");
        harness.Close();

        Assert.Contains("acme/foo", options);
        Assert.Equal("acme/foo", selected);
    });

    [Fact]
    public void SelectingARepo_KeepsTheOperatorsChoice_AcrossARepopulation() => HeadlessAvalonia.Run(() =>
    {
        // Mirrors SelectingALabel_KeepsTheOperatorsChoice_AcrossARepopulation (AC-519): the fix for the first
        // population must not turn every later one back into re-imposing the linked repository over a choice the
        // operator already made.
        var repoA = First with { Repository = "acme/foo" };
        var repoB = Second with { Repository = "acme/bar" };
        var harness = DialogHarness.Open("acme", repoA, repoB);
        harness.SetLinkedRepository("acme/foo");
        harness.PopulateRepoFilter("acme/foo", "acme/bar");
        harness.RepoFilter.SelectedItem = "acme/bar";

        harness.PopulateRepoFilter("acme/foo", "acme/bar");

        var selected = harness.RepoFilter.SelectedItem as string;
        _out.WriteLine($"selected after repopulation={selected ?? "<null>"}");
        harness.Close();

        Assert.Equal("acme/bar", selected);
    });

    [Fact]
    public void LabelFilter_OffersARepoLabel_ThatNoLoadedIssueCarries() => HeadlessAvalonia.Run(() =>
    {
        // AC-519's whole point: the label filter has to show labels from the repositories themselves, not the
        // labels that happen to appear on the (possibly capped) loaded issues — the exact gap the YouTrack status
        // filter had. Neither First nor Second carries any label at all.
        var harness = DialogHarness.Open(First, Second);

        harness.PopulateLabelFilter("docs", "triage");

        var options = harness.LabelFilter.ItemsSource?.OfType<string>().ToList() ?? [];
        _out.WriteLine($"options=[{string.Join(", ", options)}]");
        harness.Close();

        Assert.Contains("docs", options);
        Assert.Contains("triage", options);
    });

    [Fact]
    public void LabelFilter_PreselectsTheSettingsInProgressLabel_WhenItIsAmongTheRepoLabels() => HeadlessAvalonia.Run(() =>
    {
        // AC-519 step 5: reuses the existing "which label means in-progress" setting as the preselected/highlighted
        // option, rather than a second setting for the same thing.
        var settings = new GitHubIssuesSettings(new InMemoryPluginStorage()) { InProgressLabel = "in progress" };
        var harness = DialogHarness.Open(settings, "octocat", First, Second);

        harness.PopulateLabelFilter("bug", "in progress", "docs");

        var selected = harness.LabelFilter.SelectedItem as string;
        _out.WriteLine($"selected={selected ?? "<null>"}");
        harness.Close();

        Assert.Equal("in progress", selected);
    });

    [Fact]
    public void LabelFilter_WithoutAMatchingInProgressLabel_FallsBackToAllLabels() => HeadlessAvalonia.Run(() =>
    {
        var settings = new GitHubIssuesSettings(new InMemoryPluginStorage()) { InProgressLabel = "in progress" };
        var harness = DialogHarness.Open(settings, "octocat", First, Second);

        // This repo set does not have the operator's "in progress" label at all.
        harness.PopulateLabelFilter("bug", "docs");

        var selected = harness.LabelFilter.SelectedItem as string;
        _out.WriteLine($"selected={selected ?? "<null>"}");
        harness.Close();

        Assert.Equal("All labels", selected);
    });

    [Fact]
    public void SelectingALabel_KeepsTheOperatorsChoice_AcrossARepopulation() => HeadlessAvalonia.Run(() =>
    {
        // Mirrors _PopulateRepoFilter's own rule: once the operator has chosen, a later population (another load)
        // must not silently revert them to the preselected default.
        var harness = DialogHarness.Open(First, Second);
        harness.PopulateLabelFilter("bug", "docs");
        harness.LabelFilter.SelectedItem = "docs";

        harness.PopulateLabelFilter("bug", "docs");

        var selected = harness.LabelFilter.SelectedItem as string;
        _out.WriteLine($"selected after repopulation={selected ?? "<null>"}");
        harness.Close();

        Assert.Equal("docs", selected);
    });

    [Fact]
    public void StatusLine_AtExactlyThePageLimit_WarnsTheListMayBeIncomplete() => HeadlessAvalonia.Run(() =>
    {
        // AC-519 AC3: the one boundary worth proving to the exact number — the constant itself, not a round number
        // above it — since the notice's whole premise is "the result came back at exactly the page size". What
        // decides the warning is _possiblyTruncated (AC-519 fix), set here the way a real fetch at exactly the raw
        // page limit, with nothing filtered out of it, would leave it.
        var issues = Enumerable.Range(1, GitHubGhClient.IssueSearchLimit)
            .Select(number => new GitHubIssue(number, $"Issue {number}", $"https://x/{number}", null, "octocat/hello-world"))
            .ToArray();
        var settings = new GitHubIssuesSettings(new InMemoryPluginStorage()) { UseGitHubCli = true };
        var harness = DialogHarness.Open(settings, "octocat", issues);
        harness.SetPossiblyTruncated(true);

        harness.ReportLoaded();

        var status = harness.StatusText;
        _out.WriteLine($"status={status}");
        harness.Close();

        Assert.Contains("may be incomplete", status);
        Assert.Contains(GitHubGhClient.IssueSearchLimit.ToString(), status);
    });

    [Fact]
    public void StatusLine_OneShortOfThePageLimit_DoesNotWarn() => HeadlessAvalonia.Run(() =>
    {
        var issues = Enumerable.Range(1, GitHubGhClient.IssueSearchLimit - 1)
            .Select(number => new GitHubIssue(number, $"Issue {number}", $"https://x/{number}", null, "octocat/hello-world"))
            .ToArray();
        var settings = new GitHubIssuesSettings(new InMemoryPluginStorage()) { UseGitHubCli = true };
        var harness = DialogHarness.Open(settings, "octocat", issues);
        harness.SetPossiblyTruncated(false);

        harness.ReportLoaded();

        var status = harness.StatusText;
        _out.WriteLine($"status={status}");
        harness.Close();

        Assert.DoesNotContain("may be incomplete", status);
    });

    [Fact]
    public void StatusLine_InHttpMode_AlsoWarnsAtItsOwnPageLimit() => HeadlessAvalonia.Run(() =>
    {
        // The two paths have their own constants (AC-519, criterion 4 — a test per pad), currently both 100, which
        // means a count-based assertion cannot tell "read GitHubIssuesClient.IssuePageLimit" apart from "read
        // GitHubGhClient.IssueSearchLimit by mistake" — the two happen to agree. What this does prove: the ternary's
        // HTTP-mode branch runs and warns at its own boundary rather than only ever exercising the gh one.
        var issues = Enumerable.Range(1, GitHubIssuesClient.IssuePageLimit)
            .Select(number => new GitHubIssue(number, $"Issue {number}", $"https://x/{number}", null, "octocat/hello-world"))
            .ToArray();
        var settings = new GitHubIssuesSettings(new InMemoryPluginStorage()) { UseGitHubCli = false };
        var harness = DialogHarness.Open(settings, "octocat", issues);
        harness.SetPossiblyTruncated(true);

        harness.ReportLoaded();

        var status = harness.StatusText;
        _out.WriteLine($"status={status}");
        harness.Close();

        Assert.Contains("may be incomplete", status);
    });

    [Fact]
    public void StatusLine_ClientSignalsTruncation_WarnsEvenThoughTheLoadedCountIsFarBelowTheLimit() => HeadlessAvalonia.Run(() =>
    {
        // AC-519 fix, adversarial review: the exact under-warning bug — a raw page full of pull requests/archived
        // issues filters down to well under the limit (60, here), and only the client-reported signal (not
        // _all.Count) can still say "this was capped". Proves the dialog no longer reconstructs the signal from
        // what filtering left behind.
        var issues = Enumerable.Range(1, 60)
            .Select(number => new GitHubIssue(number, $"Issue {number}", $"https://x/{number}", null, "octocat/hello-world"))
            .ToArray();
        var settings = new GitHubIssuesSettings(new InMemoryPluginStorage()) { UseGitHubCli = true };
        var harness = DialogHarness.Open(settings, "octocat", issues);
        harness.SetPossiblyTruncated(true);

        harness.ReportLoaded();

        var status = harness.StatusText;
        _out.WriteLine($"status={status}");
        harness.Close();

        Assert.Contains("may be incomplete", status);
    });

    [Fact]
    public void StatusLine_LoadedCountCoincidentallyAtTheLimit_DoesNotWarnWithoutTheClientSignal() => HeadlessAvalonia.Run(() =>
    {
        // The mirror of the previous test: _all.Count landing exactly on the limit is no longer, by itself, enough
        // to warn — only _possiblyTruncated decides. This is what proves the decision moved off _all.Count entirely,
        // not just that it still happens to agree with it in the common case.
        var issues = Enumerable.Range(1, GitHubGhClient.IssueSearchLimit)
            .Select(number => new GitHubIssue(number, $"Issue {number}", $"https://x/{number}", null, "octocat/hello-world"))
            .ToArray();
        var settings = new GitHubIssuesSettings(new InMemoryPluginStorage()) { UseGitHubCli = true };
        var harness = DialogHarness.Open(settings, "octocat", issues);
        harness.SetPossiblyTruncated(false);

        harness.ReportLoaded();

        var status = harness.StatusText;
        _out.WriteLine($"status={status}");
        harness.Close();

        Assert.DoesNotContain("may be incomplete", status);
    });

    /// <summary>
    /// One dialog under test, in a window its real size, with the loaded issue set planted and the fakes it talks to
    /// kept to hand.
    /// </summary>
    private sealed class DialogHarness
    {
        private DialogHarness(Window window, GitHubIssuesDialogControl dialog, GitHubIssuesSettings settings, FakeCockpitHost host, SessionIssueLinks links)
        {
            _window = window;
            _dialog = dialog;
            Settings = settings;
            Host = host;
            Links = links;
        }

        private readonly Window _window;
        private readonly GitHubIssuesDialogControl _dialog;

        public GitHubIssuesSettings Settings { get; }

        public FakeCockpitHost Host { get; }

        public SessionIssueLinks Links { get; }

        public FakeSessionObserver Observer => Host.Observer;

        public DataGrid Grid => _window.GetVisualDescendants().OfType<DataGrid>().First();

        /// <summary>The AC-519 label filter — named so it is found reliably alongside the repository filter, the other <see cref="ComboBox"/> in the same bar.</summary>
        public ComboBox LabelFilter => _window.GetVisualDescendants().OfType<ComboBox>().First(combo => combo.Name == "labelFilter");

        /// <summary>The AC-317 repository filter.</summary>
        public ComboBox RepoFilter => _window.GetVisualDescendants().OfType<ComboBox>().First(combo => combo.Name == "repoFilter");

        /// <summary>The window-level status line — what a load's outcome (including the AC-519 truncation notice) is reported through.</summary>
        public string? StatusText => _window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Name == "status")?.Text;

        public static DialogHarness Open(params GitHubIssue[] issues) => Open("octocat", issues);

        /// <summary>
        /// Opens with a filter term of the caller's choosing. The grid is filled by typing, and the filter matches
        /// title, repository or number — so an issue whose repository is empty needs a term that is not the owner.
        /// </summary>
        public static DialogHarness Open(string filter, params GitHubIssue[] issues)
        {
            // Settings on their defaults: the CLI is off and no repository is set, so the dialog's own load
            // short-circuits on "No repository set" before any call goes out — nothing is fetched, and the issue
            // set under test is the one planted below.
            var settings = new GitHubIssuesSettings(new InMemoryPluginStorage());
            return Open(settings, filter, issues);
        }

        /// <summary>Opens with settings the caller configured first — e.g. an <see cref="GitHubIssuesSettings.InProgressLabel"/> to prove the label filter's preselection (AC-519).</summary>
        public static DialogHarness Open(GitHubIssuesSettings settings, string filter, params GitHubIssue[] issues)
        {
            var host = new FakeCockpitHost();
            var links = new SessionIssueLinks(host);
            var dialog = new GitHubIssuesDialogControl(settings, host, links);

            var window = new Window { Width = 1280, Height = 860, Content = dialog };
            window.Show();
            window.UpdateLayout();

            var harness = new DialogHarness(window, dialog, settings, host, links);
            harness._PlantLoadedIssues(issues);
            harness.Type(filter);
            return harness;
        }

        /// <summary>
        /// Drives the private label-filter population directly (AC-519) — the real fetch behind it goes through
        /// <c>gh</c>/HTTP with no seam for a test to hand it a fake, so this proves the rendering/preselection half
        /// of the feature the way <see cref="_PlantLoadedIssues"/> already proves the issue-list half.
        /// </summary>
        public void PopulateLabelFilter(params string[] labels)
        {
            var method = typeof(GitHubIssuesDialogControl).GetMethod("_PopulateLabelFilter", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("GitHubIssuesDialogControl no longer has _PopulateLabelFilter.");
            method.Invoke(_dialog, [(IReadOnlyList<string>)labels]);
            Layout();
        }

        /// <summary>Drives the private post-load status composition directly (AC-519) — proves the exact-limit truncation notice without a live fetch.</summary>
        public void ReportLoaded()
        {
            var method = typeof(GitHubIssuesDialogControl).GetMethod("_ReportLoaded", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("GitHubIssuesDialogControl no longer has _ReportLoaded.");
            method.Invoke(_dialog, []);
        }

        /// <summary>
        /// Plants the AC-519 truncation signal a real fetch would have handed back — measured by the client against
        /// the raw page it received, before any local filtering. There is no live fetch here (see the class doc), so
        /// this is planted alongside <see cref="_PlantLoadedIssues"/> the same way that field is: directly, by name.
        /// </summary>
        public void SetPossiblyTruncated(bool value)
        {
            var field = typeof(GitHubIssuesDialogControl).GetField("_possiblyTruncated", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("GitHubIssuesDialogControl no longer has _possiblyTruncated.");
            field.SetValue(_dialog, value);
        }

        /// <summary>Plants what AC-317 would have resolved from the linked project's own repository field, bypassing <c>_host.GetProjectFieldValueAsync</c> (there is no live session/project here).</summary>
        public void SetLinkedRepository(string repository)
        {
            var field = typeof(GitHubIssuesDialogControl).GetField("_linkedRepository", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("GitHubIssuesDialogControl no longer has _linkedRepository.");
            field.SetValue(_dialog, repository);
        }

        /// <summary>
        /// Drives the private repo-filter population directly (AC-317) — same reasoning as
        /// <see cref="PopulateLabelFilter"/>: the real fetch behind the repository list (gh's own repository list,
        /// or the one repository HTTP mode's settings name) goes through <c>gh</c>/HTTP with no seam for a test to
        /// hand it a fake, so the repositories are handed in directly here instead, the same way the real
        /// <c>_LoadAsync</c> hands in a list from its own independent fetch rather than deriving it from <c>_all</c>
        /// (the adversarial-review fix: deriving it from <c>_all</c> made a repository with no currently-matching
        /// issue vanish from the dropdown along with its issues).
        /// </summary>
        public void PopulateRepoFilter(params string[] repositories)
        {
            var method = typeof(GitHubIssuesDialogControl).GetMethod("_PopulateRepoFilter", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("GitHubIssuesDialogControl no longer has _PopulateRepoFilter.");
            method.Invoke(_dialog, [(IReadOnlyList<string>)repositories]);
            Layout();
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
            Assert.True(button.IsEnabled, $"\"{label}\" has to be clickable for this test to mean anything");
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

        private void _PlantLoadedIssues(GitHubIssue[] issues)
        {
            var loaded = typeof(GitHubIssuesDialogControl).GetField("_all", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("GitHubIssuesDialogControl no longer keeps its loaded issues in _all.");
            loaded.SetValue(_dialog, issues);
        }
    }
}
