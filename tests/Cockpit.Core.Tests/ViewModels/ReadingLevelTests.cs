using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using FluentAssertions;
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
        entry.RequiredApproval.Should().BeFalse();
        entry.IsAutoTool.Should().BeTrue();

        entry.ReadingLevel = ReadingLevel.Developer;
        entry.IsRowVisible.Should().BeTrue();
        entry.ShowToolBlock.Should().BeTrue();

        entry.ReadingLevel = ReadingLevel.Focus;
        entry.IsRowVisible.Should().BeTrue();
        entry.ShowToolBlock.Should().BeTrue();

        entry.ReadingLevel = ReadingLevel.Simple;
        entry.IsRowVisible.Should().BeFalse();
        entry.ShowToolBlock.Should().BeFalse();
    }

    [Fact]
    public void ConsentTool_StaysVisibleAtEveryLevel_AndSpeaksPlainlyInSimple()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "edit") { ToolName = "Edit", IsPendingPermission = true };
        entry.RequiredApproval.Should().BeTrue();

        foreach (var level in new[] { ReadingLevel.Developer, ReadingLevel.Focus, ReadingLevel.Simple })
        {
            entry.ReadingLevel = level;
            entry.IsRowVisible.Should().BeTrue($"a consent tool must stay visible at {level}");
        }

        entry.ReadingLevel = ReadingLevel.Simple;
        entry.ShowHumanToolLine.Should().BeTrue();
        entry.ShowToolBlock.Should().BeFalse();
        entry.HumanToolText.Should().Be("Changed a file — waiting for your approval");

        entry.ReadingLevel = ReadingLevel.Developer;
        entry.ShowHumanToolLine.Should().BeFalse();
        entry.ShowToolBlock.Should().BeTrue();
    }

    [Fact]
    public void ResolvedConsent_ReadsApprovedOrDeclined_InSimple()
    {
        var allowed = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "edit") { ToolName = "Edit", PermissionDecision = "Allowed", ReadingLevel = ReadingLevel.Simple };
        allowed.HumanToolText.Should().Be("✓ Changed a file — you approved this");

        var denied = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "bash") { ToolName = "Bash", PermissionDecision = "Denied", ReadingLevel = ReadingLevel.Simple };
        denied.HumanToolText.Should().Be("✕ Ran a command — you declined this");
    }

    [Fact]
    public void AssistantText_IsVisibleAtEveryLevel()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "hello");
        foreach (var level in new[] { ReadingLevel.Developer, ReadingLevel.Focus, ReadingLevel.Simple })
        {
            entry.ReadingLevel = level;
            entry.IsRowVisible.Should().BeTrue();
        }
    }

    [Fact]
    public void Thinking_IsVisibleOnlyAtDeveloper()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Thinking, "Pondering...");

        entry.ReadingLevel = ReadingLevel.Developer;
        entry.IsRowVisible.Should().BeTrue("thinking is restored at the developer surface (AC-213)");

        entry.ReadingLevel = ReadingLevel.Focus;
        entry.IsRowVisible.Should().BeFalse("Focus stays calm (AC-138)");

        entry.ReadingLevel = ReadingLevel.Simple;
        entry.IsRowVisible.Should().BeFalse("Simple stays calm (AC-138)");
    }

    [Fact]
    public void Thinking_IsNeitherMarkdownNorAPlainTextRow()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Thinking, "reasoning");

        // It renders in its own dimmed section, so it must not also match the assistant-markdown or plain-text templates.
        entry.IsThinking.Should().BeTrue();
        entry.IsAssistantMarkdown.Should().BeFalse();
        entry.IsPlainNonMarkdown.Should().BeFalse();
        entry.IsTopTimestampRow.Should().BeFalse();
    }

    [Fact]
    public void Focus_FoldsARunOfAutoToolCalls_UnderOneAnchor()
    {
        var vm = NewSession();
        vm.ReadingLevel = ReadingLevel.Focus;
        var rows = AddAutoRuns(vm, 3);

        rows[0].IsGroupAnchor.Should().BeTrue();
        rows[0].IsInGroup.Should().BeTrue();
        rows[0].GroupCount.Should().Be(3);
        rows[0].ShowGroupSummary.Should().BeTrue();
        rows[0].GroupSummaryText.Should().Be("3 steps run");
        rows[0].IsRowVisible.Should().BeTrue();

        rows[1].IsInGroup.Should().BeTrue();
        rows[1].IsGroupAnchor.Should().BeFalse();
        rows[1].IsRowVisible.Should().BeFalse("a folded member hides until the run is expanded");
        rows[2].IsRowVisible.Should().BeFalse();
    }

    [Fact]
    public void Focus_ExpandingTheAnchor_RevealsTheWholeRun()
    {
        var vm = NewSession();
        vm.ReadingLevel = ReadingLevel.Focus;
        var rows = AddAutoRuns(vm, 3);

        rows[0].GroupToggleRequested!.Invoke();

        rows.Should().OnlyContain(row => row.IsGroupExpanded);
        rows[1].IsRowVisible.Should().BeTrue();
        rows[0].ShowToolBlock.Should().BeTrue();
    }

    [Fact]
    public void Focus_AConsentToolBreaksTheRun_SoNeitherSideFolds()
    {
        var vm = NewSession();
        vm.ReadingLevel = ReadingLevel.Focus;
        vm.Transcript.Add(AutoTool("Bash"));
        vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "edit") { ToolName = "Edit", IsPendingPermission = true });
        vm.Transcript.Add(AutoTool("Grep"));

        vm.Transcript.Should().OnlyContain(row => !row.IsInGroup);
    }

    [Fact]
    public void Developer_DoesNotFold_AndKeepsEveryRowVisible()
    {
        var vm = NewSession();
        var rows = AddAutoRuns(vm, 3);

        rows.Should().OnlyContain(row => !row.IsInGroup && row.IsRowVisible);
    }

    [Fact]
    public void AToolThatTurnsIntoAConsentPrompt_LeavesTheFoldGroup()
    {
        var vm = NewSession();
        vm.ReadingLevel = ReadingLevel.Focus;
        var rows = AddAutoRuns(vm, 2);
        rows[1].IsInGroup.Should().BeTrue();

        // The permission request lands after the tool-use event; the row must fall out of the auto-fold run.
        rows[1].IsPendingPermission = true;

        rows[0].IsInGroup.Should().BeFalse();
        rows[1].IsInGroup.Should().BeFalse();
        rows[1].IsRowVisible.Should().BeTrue();
    }

    [Fact]
    public async Task StartConfigured_SeedsTheReadingLevelFromTheProfileDefault()
    {
        var profile = Profile with { Defaults = new ProfileDefaults(string.Empty, string.Empty, string.Empty) { DefaultReadingLevel = ReadingLevel.Simple } };
        var vm = NewSession();

        await vm.StartConfiguredAsync(profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        vm.ReadingLevel.Should().Be(ReadingLevel.Simple);
    }

    [Fact]
    public async Task StartConfigured_PerSessionOverride_WinsOverTheProfileDefault()
    {
        var profile = Profile with { Defaults = new ProfileDefaults(string.Empty, string.Empty, string.Empty) { DefaultReadingLevel = ReadingLevel.Focus } };
        var vm = NewSession();

        await vm.StartConfiguredAsync(profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort, readingLevel: ReadingLevel.Simple);

        vm.ReadingLevel.Should().Be(ReadingLevel.Simple);
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
