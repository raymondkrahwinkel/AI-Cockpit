using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The session reading levels (AC-138): what a single transcript row shows at Developer / Focus / Simple, and how
/// <see cref="SessionViewModel"/> folds runs of auto-executed tool calls under one "N steps run" line at Focus. The
/// core rule under test is that a tool call which asked for approval — pending or already allowed/denied — stays
/// visible at every level, in plain words at Simple, while a tool call that ran on its own is folded or hidden.
/// </summary>
public class ReadingLevelTests
{
    private static readonly SessionProfile Profile = new("default", new ClaudeConfig(@"C:\fake\.claude"));

    private static TranscriptEntryViewModel AutoTool(string name = "Bash") =>
        new(TranscriptEntryKind.ToolUse, "ran something") { ToolName = name, ToolUseId = name, InputJson = "{}" };

    [Fact]
    public void AutoTool_IsHiddenInSimple_AndShownOtherwise()
    {
        var entry = AutoTool();
        Assert.False(entry.RequiredApproval);
        Assert.True(entry.IsAutoTool);

        entry.ReadingLevel = ReadingLevel.Developer;
        Assert.True(entry.IsRowVisible);
        Assert.True(entry.ShowToolBlock);

        entry.ReadingLevel = ReadingLevel.Focus;
        Assert.True(entry.IsRowVisible);
        Assert.True(entry.ShowToolBlock);

        entry.ReadingLevel = ReadingLevel.Simple;
        Assert.False(entry.IsRowVisible);
        Assert.False(entry.ShowToolBlock);
    }

    [Fact]
    public void ConsentTool_StaysVisibleAtEveryLevel_AndSpeaksPlainlyInSimple()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "edit") { ToolName = "Edit", IsPendingPermission = true };
        Assert.True(entry.RequiredApproval);

        foreach (var level in new[] { ReadingLevel.Developer, ReadingLevel.Focus, ReadingLevel.Simple })
        {
            entry.ReadingLevel = level;
            Assert.True(entry.IsRowVisible, $"a consent tool must stay visible at {level}");
        }

        entry.ReadingLevel = ReadingLevel.Simple;
        Assert.True(entry.ShowHumanToolLine);
        Assert.False(entry.ShowToolBlock);
        Assert.Equal("Changed a file — waiting for your approval", entry.HumanToolText);

        entry.ReadingLevel = ReadingLevel.Developer;
        Assert.False(entry.ShowHumanToolLine);
        Assert.True(entry.ShowToolBlock);
    }

    [Fact]
    public void ResolvedConsent_ReadsApprovedOrDeclined_InSimple()
    {
        var allowed = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "edit") { ToolName = "Edit", PermissionDecision = "Allowed", ReadingLevel = ReadingLevel.Simple };
        Assert.Equal("✓ Changed a file — you approved this", allowed.HumanToolText);

        var denied = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "bash") { ToolName = "Bash", PermissionDecision = "Denied", ReadingLevel = ReadingLevel.Simple };
        Assert.Equal("✕ Ran a command — you declined this", denied.HumanToolText);
    }

    [Fact]
    public void AssistantText_IsVisibleAtEveryLevel()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "hello");
        foreach (var level in new[] { ReadingLevel.Developer, ReadingLevel.Focus, ReadingLevel.Simple })
        {
            entry.ReadingLevel = level;
            Assert.True(entry.IsRowVisible);
        }
    }

    [Fact]
    public void Thinking_IsVisibleOnlyAtDeveloper()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Thinking, "Pondering...");

        entry.ReadingLevel = ReadingLevel.Developer;
        Assert.True(entry.IsRowVisible, "thinking is restored at the developer surface (AC-213)");

        entry.ReadingLevel = ReadingLevel.Focus;
        Assert.False(entry.IsRowVisible, "Focus stays calm (AC-138)");

        entry.ReadingLevel = ReadingLevel.Simple;
        Assert.False(entry.IsRowVisible, "Simple stays calm (AC-138)");
    }

    [Fact]
    public void Thinking_IsNeitherMarkdownNorAPlainTextRow()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Thinking, "reasoning");

        // It renders in its own dimmed section, so it must not also match the assistant-markdown or plain-text templates.
        Assert.True(entry.IsThinking);
        Assert.False(entry.IsAssistantMarkdown);
        Assert.False(entry.IsPlainNonMarkdown);
        Assert.False(entry.IsTopTimestampRow);
    }

    [Fact]
    public void Focus_FoldsARunOfAutoToolCalls_UnderOneAnchor()
    {
        var vm = NewSession();
        vm.ReadingLevel = ReadingLevel.Focus;
        var rows = AddAutoRuns(vm, 3);

        Assert.True(rows[0].IsGroupAnchor);
        Assert.True(rows[0].IsInGroup);
        Assert.Equal(3, rows[0].GroupCount);
        Assert.True(rows[0].ShowGroupSummary);
        Assert.Equal("3 steps run", rows[0].GroupSummaryText);
        Assert.True(rows[0].IsRowVisible);

        Assert.True(rows[1].IsInGroup);
        Assert.False(rows[1].IsGroupAnchor);
        Assert.False(rows[1].IsRowVisible, "a folded member hides until the run is expanded");
        Assert.False(rows[2].IsRowVisible);
    }

    [Fact]
    public void Focus_ExpandingTheAnchor_RevealsTheWholeRun()
    {
        var vm = NewSession();
        vm.ReadingLevel = ReadingLevel.Focus;
        var rows = AddAutoRuns(vm, 3);

        rows[0].GroupToggleRequested!.Invoke();

        Assert.All(rows, row => Assert.True(row.IsGroupExpanded));
        Assert.True(rows[1].IsRowVisible);
        Assert.True(rows[0].ShowToolBlock);
    }

    [Fact]
    public void Focus_AConsentToolBreaksTheRun_SoNeitherSideFolds()
    {
        var vm = NewSession();
        vm.ReadingLevel = ReadingLevel.Focus;
        vm.Transcript.Add(AutoTool("Bash"));
        vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "edit") { ToolName = "Edit", IsPendingPermission = true });
        vm.Transcript.Add(AutoTool("Grep"));

        Assert.All(vm.Transcript, row => Assert.False(row.IsInGroup));
    }

    [Fact]
    public void Developer_DoesNotFold_AndKeepsEveryRowVisible()
    {
        var vm = NewSession();
        var rows = AddAutoRuns(vm, 3);

        Assert.All(rows, row => Assert.True(!row.IsInGroup && row.IsRowVisible));
    }

    [Fact]
    public void AToolThatTurnsIntoAConsentPrompt_LeavesTheFoldGroup()
    {
        var vm = NewSession();
        vm.ReadingLevel = ReadingLevel.Focus;
        var rows = AddAutoRuns(vm, 2);
        Assert.True(rows[1].IsInGroup);

        // The permission request lands after the tool-use event; the row must fall out of the auto-fold run.
        rows[1].IsPendingPermission = true;

        Assert.False(rows[0].IsInGroup);
        Assert.False(rows[1].IsInGroup);
        Assert.True(rows[1].IsRowVisible);
    }

    [Fact]
    public void Focus_ASecondRunGrowingRowByRow_LeavesTheEarlierRunGrouped()
    {
        // Rows are only re-folded around where they land (AC-787), so a run forming late in a long transcript must
        // not cost the earlier ones their grouping.
        var vm = NewSession();
        vm.ReadingLevel = ReadingLevel.Focus;
        vm.Transcript.Add(AutoTool("Read"));
        vm.Transcript.Add(AutoTool("Bash"));
        vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "thinking out loud"));
        vm.Transcript.Add(AutoTool("Grep"));
        vm.Transcript.Add(AutoTool("Glob"));
        vm.Transcript.Add(AutoTool("Edit"));

        Assert.True(vm.Transcript[0].IsGroupAnchor);
        Assert.Equal(2, vm.Transcript[0].GroupCount);
        Assert.True(vm.Transcript[1].IsInGroup);
        Assert.False(vm.Transcript[2].IsInGroup);
        Assert.True(vm.Transcript[3].IsGroupAnchor);
        Assert.Equal(3, vm.Transcript[3].GroupCount);
        Assert.All(vm.Transcript.Skip(3), row => Assert.True(row.IsInGroup));
    }

    [Fact]
    public void Focus_ARowThatStopsAskingForApproval_JoinsTheRunsEitherSideOfIt()
    {
        // The row that changed is the one re-folded, so the rows around it — a run each side, previously broken
        // apart by this one — have to be taken in with it.
        var vm = NewSession();
        vm.ReadingLevel = ReadingLevel.Focus;
        vm.Transcript.Add(AutoTool("Bash"));
        var consent = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "edit") { ToolName = "Edit", IsPendingPermission = true };
        vm.Transcript.Add(consent);
        vm.Transcript.Add(AutoTool("Grep"));
        Assert.All(vm.Transcript, row => Assert.False(row.IsInGroup));

        consent.IsPendingPermission = false;

        Assert.True(vm.Transcript[0].IsGroupAnchor);
        Assert.Equal(3, vm.Transcript[0].GroupCount);
        Assert.All(vm.Transcript, row => Assert.True(row.IsInGroup));
    }

    [Fact]
    public async Task StartConfigured_SeedsTheReadingLevelFromTheProfileDefault()
    {
        var profile = Profile with { Defaults = new ProfileDefaults(string.Empty, string.Empty, string.Empty) { DefaultReadingLevel = ReadingLevel.Simple } };
        var vm = NewSession();

        await vm.StartConfiguredAsync(profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.Equal(ReadingLevel.Simple, vm.ReadingLevel);
    }

    [Fact]
    public async Task StartConfigured_PerSessionOverride_WinsOverTheProfileDefault()
    {
        var profile = Profile with { Defaults = new ProfileDefaults(string.Empty, string.Empty, string.Empty) { DefaultReadingLevel = ReadingLevel.Focus } };
        var vm = NewSession();

        await vm.StartConfiguredAsync(profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort, readingLevel: ReadingLevel.Simple);

        Assert.Equal(ReadingLevel.Simple, vm.ReadingLevel);
    }

    private static IReadOnlyList<TranscriptEntryViewModel> AddAutoRuns(SessionViewModel vm, int count)
    {
        var rows = new List<TranscriptEntryViewModel>();
        for (var i = 0; i < count; i++)
        {
            var row = AutoTool($"Bash{i}");
            rows.Add(row);
            vm.Transcript.Add(row);
        }

        return rows;
    }

    private static SessionViewModel NewSession()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(EmptyEvents());
        return new SessionViewModel(new SessionManager(FactoryFor(driver)));
    }

    private static async IAsyncEnumerable<SessionEvent> EmptyEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static ISessionDriverFactory FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }
}
