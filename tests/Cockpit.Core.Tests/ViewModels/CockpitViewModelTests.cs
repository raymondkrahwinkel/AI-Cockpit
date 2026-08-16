using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Configuration;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Projects;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Layout;
using Cockpit.Core.Voice;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Exercises <see cref="CockpitViewModel"/>'s session-manager surface (new/select/close) against a
/// fake session factory and a fake <see cref="ISessionDialogService"/>. Since #31 the app opens no
/// session on startup and creating one goes through the New-session dialog, so tests that need a panel
/// confirm one first via <c>NewSessionCommand</c> (the fake dialog returns a canned result).
/// </summary>
public class CockpitViewModelTests
{
    [Fact]
    public void Constructor_OpensNoSessionOnStartup()
    {
        var vm = NewVm();

        Assert.Empty(vm.Sessions);
        Assert.False(vm.HasSessions);
        Assert.Null(vm.SelectedSession);
    }

    [Fact]
    public async Task AboutCommand_ShowsTheAboutDialog()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        var vm = NewVm(dialogService);

        await vm.AboutCommand.ExecuteAsync(null);

        await dialogService.Received(1).ShowAboutDialogAsync();
    }

    [Fact]
    public async Task GlossaryCommand_ShowsTheGlossaryDialog()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        var vm = NewVm(dialogService);

        await vm.ShowGlossaryCommand.ExecuteAsync(null);

        await dialogService.Received(1).ShowGlossaryDialogAsync();
    }

    [Fact]
    public async Task RunSetupAgainCommand_ReopensTheFirstRunWizard()
    {
        var wizard = Substitute.For<IFirstRunWizard>();
        var vm = NewVm(firstRunWizard: wizard);

        await vm.RunSetupAgainCommand.ExecuteAsync(null);

        await wizard.Received(1).ShowAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSetupAgainCommand_WithNoWizardWired_DoesNothing()
    {
        // Not registered yet (the wizard is a parallel strand's) is a design/host state this command has to
        // survive rather than crash on — the same "no service, no-op" rule every other Help-menu command follows.
        var vm = NewVm();

        await vm.RunSetupAgainCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task GuideCommand_WhenTheBrowserOpens_ShowsNoNotice()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        var vm = NewVm(dialogService, tryOpenExternalLink: _ => true);

        await vm.OpenGuideCommand.ExecuteAsync(null);

        await dialogService.DidNotReceive().ShowConfirmationDialogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task GuideCommand_WhenTheBrowserWontOpen_SaysSoInsteadOfOpeningNothing()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        var vm = NewVm(dialogService, tryOpenExternalLink: _ => false);

        await vm.OpenGuideCommand.ExecuteAsync(null);

        await dialogService.Received(1).ShowConfirmationDialogAsync(
            Arg.Any<string>(),
            Arg.Is<string>(message => message.Contains(CockpitBrand.GuideUrl, StringComparison.Ordinal)),
            "OK");
    }

    [Fact]
    public async Task GuideCommand_AsksForTheCockpitBrandGuideUrl_NotALiteral()
    {
        string? askedUrl = null;
        var vm = NewVm(tryOpenExternalLink: url =>
        {
            askedUrl = url;
            return true;
        });

        await vm.OpenGuideCommand.ExecuteAsync(null);

        Assert.Equal(CockpitBrand.GuideUrl, askedUrl);
    }

    [Fact]
    public async Task NewSession_WhenTheDialogIsCancelled_AddsNoSession()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns((NewSessionResult?)null);
        var vm = NewVm(dialogService);

        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.Empty(vm.Sessions);
        Assert.False(vm.HasSessions);
    }

    [Fact]
    public async Task ShowNewSessionDialogForPlugin_ReturnsTheStartedSessionsPaneId_AndInjectsTheInitialPrompt()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync(Arg.Any<NewSessionPrefill?>(), Arg.Any<bool>())
            .Returns(NewSessionResultFor(SessionKind.Sdk));
        var vm = NewVm(dialogService);

        var prefill = new NewSessionPrefill(ProfileLabel: "default", InitialPrompt: "Investigate AC-96");
        var paneId = await vm.ShowNewSessionDialogForPluginAsync(prefill);

        // The id handed back is the started session's own PaneId (== ICockpitSessionObserver.ActivePaneId), so a
        // plugin's onStarted can act on that exact pane — the load-bearing #AC-96 contract.
        var session = vm.Sessions.Single();
        Assert.Equal(session.PaneId, paneId);

        // The prefill's initial prompt lands in that session's composer through the inject seam, for the operator to send.
        Assert.Equal("Investigate AC-96", ((SessionViewModel)session).InputText);

        // The prefill is forwarded to the dialog so its fields are pre-filled for the operator.
        await dialogService.Received(1).ShowNewSessionDialogAsync(prefill, Arg.Any<bool>());
    }

    [Fact]
    public async Task ShowNewSessionDialogForPlugin_WhenCancelled_ReturnsNullAndAddsNoSession()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync(Arg.Any<NewSessionPrefill?>(), Arg.Any<bool>())
            .Returns((NewSessionResult?)null);
        var vm = NewVm(dialogService);

        var paneId = await vm.ShowNewSessionDialogForPluginAsync(new NewSessionPrefill(ProfileLabel: "default"));

        Assert.Null(paneId);
        Assert.Empty(vm.Sessions);
    }

    [Fact]
    public async Task ShowNewSessionDialogForPlugin_WithALinkedProject_OpensTheDialogOnIt()
    {
        // AC-419: the plugin names the project the only way it can — by what the operator linked it as — and the
        // dialog opens on it, folder/profile/worktree defaults and all, exactly as picking it by hand would.
        var cockpit = TrackedIn("Cockpit", "AC");
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync(Arg.Any<NewSessionPrefill?>(), Arg.Any<bool>(), Arg.Any<Project?>())
            .Returns(NewSessionResultFor(SessionKind.Sdk));
        var vm = NewVm(dialogService, projects: await LoadedProjectsAsync(TrackedIn("Depot", "DEP"), cockpit));

        var prefill = new NewSessionPrefill(SessionName: "AC-419")
        {
            LinkedProject = new ProjectLink("youtrack.project", "AC"),
        };
        await vm.ShowNewSessionDialogForPluginAsync(prefill);

        await dialogService.Received(1).ShowNewSessionDialogAsync(prefill, Arg.Any<bool>(), cockpit);
    }

    [Fact]
    public async Task ShowNewSessionDialogForPlugin_WithALinkNoProjectDeclares_OpensOnNoProject()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync(Arg.Any<NewSessionPrefill?>(), Arg.Any<bool>(), Arg.Any<Project?>())
            .Returns(NewSessionResultFor(SessionKind.Sdk));
        var vm = NewVm(dialogService, projects: await LoadedProjectsAsync(TrackedIn("Depot", "DEP")));

        await vm.ShowNewSessionDialogForPluginAsync(new NewSessionPrefill
        {
            LinkedProject = new ProjectLink("youtrack.project", "AC"),
        });

        await dialogService.Received(1).ShowNewSessionDialogAsync(Arg.Any<NewSessionPrefill?>(), Arg.Any<bool>(), null);
    }

    [Fact]
    public async Task ShowNewSessionDialogForPlugin_WithTwoProjectsOnTheSameLink_PicksNeither()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync(Arg.Any<NewSessionPrefill?>(), Arg.Any<bool>(), Arg.Any<Project?>())
            .Returns(NewSessionResultFor(SessionKind.Sdk));
        var vm = NewVm(dialogService, projects: await LoadedProjectsAsync(TrackedIn("Cockpit", "AC"), TrackedIn("Cockpit fork", "AC")));

        await vm.ShowNewSessionDialogForPluginAsync(new NewSessionPrefill
        {
            LinkedProject = new ProjectLink("youtrack.project", "AC"),
        });

        await dialogService.Received(1).ShowNewSessionDialogAsync(Arg.Any<NewSessionPrefill?>(), Arg.Any<bool>(), null);
    }

    [Fact]
    public async Task NewSession_AddsASessionSelectsItAndFlipsHasSessions()
    {
        var vm = NewVm();

        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.Single(vm.Sessions);
        Assert.True(vm.HasSessions);
        Assert.Equal(vm.Sessions[0], vm.SelectedSession);
        Assert.True(vm.SelectedSession!.IsSelected);
        Assert.Equal("default - 1", vm.SelectedSession.Title);
    }

    [Fact]
    public async Task SetSessionStatusline_ByPaneId_SetsItOnThatSession_AndAnUnknownPaneIdIsANoOp()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions.Single();

        Assert.True(vm.SetSessionStatusline(session.PaneId, "AC-13"));
        Assert.Equal("AC-13", session.Statusline);

        // An unknown pane id changes nothing and says it did nothing — a plugin/agent targeting a closed session.
        Assert.False(vm.SetSessionStatusline("no-such-pane", "AC-99"));
        Assert.Equal("AC-13", session.Statusline);

        // An empty string clears it (hides the line).
        Assert.True(vm.SetSessionStatusline(session.PaneId, ""));
        Assert.Empty(session.Statusline);
    }

    [Fact]
    public async Task SetSessionStatus_WhenTheDialogReturnsAValue_WritesItToTheSession()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Sdk));
        dialogService.ShowSetStatusDialogAsync(Arg.Any<string>()).Returns("AC-32");
        var vm = NewVm(dialogService);
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions.Single();

        await vm.SetSessionStatusCommand.ExecuteAsync(session);

        Assert.Equal("AC-32", session.Statusline);
    }

    [Fact]
    public async Task SetSessionStatus_WhenTheDialogClears_EmptiesTheStatusline()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Sdk));
        // The dialog's own Clear button returns an empty string (distinct from cancelling, which returns null).
        dialogService.ShowSetStatusDialogAsync(Arg.Any<string>()).Returns(string.Empty);
        var vm = NewVm(dialogService);
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions.Single();
        session.Statusline = "AC-13";

        await vm.SetSessionStatusCommand.ExecuteAsync(session);

        Assert.Empty(session.Statusline);
    }

    [Fact]
    public async Task SetSessionStatus_WhenCancelled_LeavesTheStatuslineUnchanged()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Sdk));
        dialogService.ShowSetStatusDialogAsync(Arg.Any<string>()).Returns((string?)null);
        var vm = NewVm(dialogService);
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions.Single();
        session.Statusline = "AC-13";

        await vm.SetSessionStatusCommand.ExecuteAsync(session);

        // Cancel seeds the dialog with the current status and returns null → the line stays as it was.
        await dialogService.Received().ShowSetStatusDialogAsync("AC-13");
        Assert.Equal("AC-13", session.Statusline);
    }

    [Fact]
    public async Task ClearSessionStatus_EmptiesTheStatusline()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions.Single();
        session.Statusline = "AC-13";

        vm.ClearSessionStatusCommand.Execute(session);

        Assert.Empty(session.Statusline);
    }

    [Fact]
    public async Task SetSessionName_ByPaneId_RenamesThatSession_AndIgnoresABlankName()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions.Single();

        Assert.True(vm.SetSessionName(session.PaneId, "  Working on AC-13  "));
        Assert.Equal("Working on AC-13", session.Title);

        // A blank name is ignored — the title stays, and it says it did nothing.
        Assert.False(vm.SetSessionName(session.PaneId, "   "));
        Assert.Equal("Working on AC-13", session.Title);
    }

    [Fact]
    public async Task SuggestSessionName_RenamesASessionStillOnTheNameTheCockpitMadeUp()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions.Single();
        Assert.Equal("default - 1", session.Title);

        Assert.True(vm.SuggestSessionName(session.PaneId, "  AC-310  "));
        Assert.Equal("AC-310", session.Title);

        // A suggested name is still one nobody chose, so linking a second ticket relabels rather than sticking
        // on the first — the session shows what it is working on now.
        Assert.True(vm.SuggestSessionName(session.PaneId, "AC-311"));
        Assert.Equal("AC-311", session.Title);

        Assert.False(vm.SuggestSessionName("no-such-pane", "AC-312"));
    }

    [Fact]
    public async Task SuggestSessionName_LeavesASessionTheOperatorNamedThemselves()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions.Single();

        // The sidebar's inline rename: a name typed on purpose, which a ticket link must not take away.
        session.EditTitle = "release work";
        session.CommitRename();

        Assert.False(vm.SuggestSessionName(session.PaneId, "AC-310"));
        Assert.Equal("release work", session.Title);
    }

    [Fact]
    public async Task SuggestSessionName_LeavesASessionNamedInTheNewSessionDialog()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync()
            .Returns(NewSessionResultFor(SessionKind.Sdk) with { SessionName = "release work" });
        var vm = NewVm(dialogService);
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions.Single();
        Assert.Equal("release work", session.Title);

        Assert.False(vm.SuggestSessionName(session.PaneId, "AC-310"));
        Assert.Equal("release work", session.Title);
    }

    [Fact]
    public async Task SuggestSessionName_LeavesASessionAnEarlierSetSessionNameClaimed()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions.Single();

        // SetSessionName is the "I mean it" call — a workflow or the agent naming the session deliberately.
        Assert.True(vm.SetSessionName(session.PaneId, "release work"));

        Assert.False(vm.SuggestSessionName(session.PaneId, "AC-310"));
        Assert.Equal("release work", session.Title);
    }

    [Fact]
    public async Task DuplicateSession_LeavesTheCopyOfAnUnnamedSessionOpenToBeingLabelled()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);

        await vm.DuplicateSessionCommand.ExecuteAsync(vm.Sessions.Single());

        // "default - 1 (copy)" is composed here, and a copy of a session nobody named is equally unnamed — so it
        // must not end up more protected from a ticket link than the session it came from (#AC-310).
        var copy = vm.Sessions.Last();
        Assert.Equal("default - 1 (copy)", copy.Title);
        Assert.True(vm.SuggestSessionName(copy.PaneId, "AC-310"));
    }

    [Fact]
    public async Task DuplicateSession_CarriesOverThatTheOriginalWasNamedOnPurpose()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var original = vm.Sessions.Single();
        original.EditTitle = "release work";
        original.CommitRename();

        await vm.DuplicateSessionCommand.ExecuteAsync(original);

        var copy = vm.Sessions.Last();
        Assert.Equal("release work (copy)", copy.Title);
        Assert.False(vm.SuggestSessionName(copy.PaneId, "AC-310"));
    }

    [Fact]
    public async Task ShowTimestamps_TogglesEveryOpenSessionLive()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.NewSessionCommand.ExecuteAsync(null);

        vm.ShowTimestamps = true;

        Assert.All(vm.Sessions, s => Assert.True(s.ShowTimestamps));
    }

    [Fact]
    public async Task ShowTimestamps_WhenOn_AppliesToASessionCreatedAfterwards()
    {
        var vm = NewVm();
        vm.ShowTimestamps = true;

        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.True(vm.Sessions.Single().ShowTimestamps);
    }

    [Fact]
    public async Task AutoCloseOnExit_TogglesEveryOpenSessionLive()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.NewSessionCommand.ExecuteAsync(null);

        vm.AutoCloseOnExit = true;

        Assert.All(vm.Sessions, s => Assert.True(s.AutoCloseOnExit));
    }

    [Fact]
    public async Task CombineQueuedMessages_TogglesOpenSdkSessionsLive_AndSeedsNewOnes()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);

        vm.CombineQueuedMessages = true; // reaches the already-open session live
        await vm.NewSessionCommand.ExecuteAsync(null); // and is seeded onto a session created afterwards

        Assert.NotEmpty(vm.Sessions.OfType<SessionViewModel>());
        Assert.All(vm.Sessions.OfType<SessionViewModel>(), s => Assert.True(s.CombineQueuedMessages));
    }

    [Fact]
    public void ShowSinglePane_IsTrueWhenEitherZoomedOrSingleLayout()
    {
        var vm = NewVm();
        Assert.False(vm.ShowSinglePane);

        vm.IsZoomed = true;
        Assert.True(vm.ShowSinglePane);

        vm.IsZoomed = false;
        vm.GlobalSingleSessionLayout = true;
        Assert.True(vm.ShowSinglePane);
    }

    [Fact]
    public void ShowZoomButton_HidesInFocusRailLayout_LikeItDoesInSingleSessionLayout()
    {
        var vm = new CockpitViewModel();
        var workspaceId = vm.Workspaces.Active!.Id;
        vm.Sessions.Add(new SessionViewModel { Title = "S1", WorkspaceId = workspaceId });
        vm.Sessions.Add(new SessionViewModel { Title = "S2", WorkspaceId = workspaceId });
        Assert.True(vm.ShowZoomButton, "two sessions in the adaptive grid can be zoomed");

        vm.GlobalFocusRailLayout = true;
        Assert.True(vm.FocusRailLayout);
        Assert.False(vm.ShowZoomButton, "focus+rail already shows one session large — Zoom would be a no-op (AC-445)");
    }

    [Fact]
    public async Task SessionCloseRequested_ClosesThatSessionThroughTheCockpit()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];

        session.RequestSelfClose();

        Assert.DoesNotContain(session, vm.Sessions);
    }

    [Fact]
    public async Task NewSession_WithADialogName_UsesItAsTheSessionTitle()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(new NewSessionResult(
            SessionKind.Sdk,
            new SessionProfile("default", new ClaudeConfig(@"C:\fake\.claude")),
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort,
            "My debug session"));
        var vm = NewVm(dialogService);

        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.Equal("My debug session", vm.Sessions[0].Title);
    }

    [Fact]
    public async Task NewSession_AssignsIncrementingTitles()
    {
        var vm = NewVm();

        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.Equal("default - 1", vm.Sessions[0].Title);
        Assert.Equal("default - 2", vm.Sessions[1].Title);
    }

    [Fact]
    public async Task SelectSession_SwitchesSelectionAndIsSelectedFlags()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.NewSessionCommand.ExecuteAsync(null);
        var first = vm.Sessions[0];
        var second = vm.Sessions[1];

        vm.SelectSessionCommand.Execute(second);

        Assert.Equal(second, vm.SelectedSession);
        Assert.False(first.IsSelected);
        Assert.True(second.IsSelected);
    }

    [Fact]
    public async Task CloseSession_RemovesItFromSessions()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];

        await vm.CloseSessionCommand.ExecuteAsync(session);

        Assert.DoesNotContain(session, vm.Sessions);
    }

    [Fact]
    public async Task CloseSession_WhenClosingTheSelectedSession_SelectsAnotherRemainingSession()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.NewSessionCommand.ExecuteAsync(null);
        var first = vm.Sessions[0];
        var second = vm.Sessions[1];
        vm.SelectSessionCommand.Execute(first);

        await vm.CloseSessionCommand.ExecuteAsync(first);

        Assert.Equal(second, vm.SelectedSession);
    }

    [Fact]
    public async Task CloseSession_WhenClosingTheLastSession_ClearsSelectionZoomAndHasSessions()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        vm.ToggleZoomCommand.Execute(null);

        await vm.CloseSessionCommand.ExecuteAsync(session);

        Assert.Null(vm.SelectedSession);
        Assert.False(vm.IsZoomed);
        Assert.False(vm.HasSessions);
    }

    [Fact]
    public async Task RequestCloseSession_WhenTheSessionIsIdle_ClosesItImmediately()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        session.SessionStatus = SessionStatus.Idle;

        await vm.RequestCloseSessionCommand.ExecuteAsync(session);

        Assert.DoesNotContain(session, vm.Sessions);
        Assert.False(session.IsConfirmingClose);
    }

    [Fact]
    public async Task RequestCloseSession_WhenTheSessionIsBusy_AsksForConfirmationAndKeepsTheSession()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        session.SessionStatus = SessionStatus.Busy;

        await vm.RequestCloseSessionCommand.ExecuteAsync(session);

        Assert.Contains(session, vm.Sessions);
        Assert.True(session.IsConfirmingClose);
    }

    [Fact]
    public async Task ConfirmCloseSession_ClosesTheSessionAndClearsTheConfirmFlag()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        session.SessionStatus = SessionStatus.Busy;
        await vm.RequestCloseSessionCommand.ExecuteAsync(session);

        await vm.ConfirmCloseSessionCommand.ExecuteAsync(session);

        Assert.DoesNotContain(session, vm.Sessions);
        Assert.False(session.IsConfirmingClose);
    }

    [Fact]
    public async Task CancelCloseSession_KeepsTheSessionAndClearsTheConfirmFlag()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        session.SessionStatus = SessionStatus.Busy;
        await vm.RequestCloseSessionCommand.ExecuteAsync(session);

        vm.CancelCloseSessionCommand.Execute(session);

        Assert.Contains(session, vm.Sessions);
        Assert.False(session.IsConfirmingClose);
    }

    [Fact]
    public async Task NewSession_WhenTheDialogPicksTty_AddsATtyPanelAndSelectsIt()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Tty));
        var vm = NewVm(dialogService);

        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.Single(vm.Sessions);
        Assert.IsType<TtyViewModel>(vm.Sessions[0]);
        Assert.Equal(vm.Sessions[0], vm.SelectedSession);
        Assert.True(vm.SelectedSession!.IsSelected);
    }

    [Fact]
    public async Task NewSession_MixingSdkAndTtyPicks_ContinuesTheSharedTitleCounter()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(
            NewSessionResultFor(SessionKind.Sdk),
            NewSessionResultFor(SessionKind.Tty));
        var vm = NewVm(dialogService);

        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.Equal("default - 1", vm.Sessions[0].Title);
        Assert.Equal("default - 2", vm.Sessions[1].Title);
    }

    [Fact]
    public void ToggleZoom_FlipsIsZoomed()
    {
        var vm = NewVm();

        vm.ToggleZoomCommand.Execute(null);
        Assert.True(vm.IsZoomed);

        vm.ToggleZoomCommand.Execute(null);
        Assert.False(vm.IsZoomed);
    }

    [Fact]
    public async Task SelectNextSession_MovesToTheFollowingSession()
    {
        var vm = await NewVmWithSessionsAsync(3);
        vm.SelectSessionCommand.Execute(vm.Sessions[0]);

        vm.SelectNextSession();

        Assert.Equal(vm.Sessions[1], vm.SelectedSession);
    }

    [Fact]
    public async Task SelectNextSession_FromTheLastSession_WrapsToTheFirst()
    {
        var vm = await NewVmWithSessionsAsync(3);
        vm.SelectSessionCommand.Execute(vm.Sessions[2]);

        vm.SelectNextSession();

        Assert.Equal(vm.Sessions[0], vm.SelectedSession);
    }

    [Fact]
    public async Task SelectPreviousSession_MovesToThePrecedingSession()
    {
        var vm = await NewVmWithSessionsAsync(3);
        vm.SelectSessionCommand.Execute(vm.Sessions[2]);

        vm.SelectPreviousSession();

        Assert.Equal(vm.Sessions[1], vm.SelectedSession);
    }

    [Fact]
    public async Task SelectPreviousSession_FromTheFirstSession_WrapsToTheLast()
    {
        var vm = await NewVmWithSessionsAsync(3);
        vm.SelectSessionCommand.Execute(vm.Sessions[0]);

        vm.SelectPreviousSession();

        Assert.Equal(vm.Sessions[2], vm.SelectedSession);
    }

    [Fact]
    public async Task SelectNextSession_KeepsIsSelectedFlagsConsistent()
    {
        var vm = await NewVmWithSessionsAsync(2);
        vm.SelectSessionCommand.Execute(vm.Sessions[0]);

        vm.SelectNextSession();

        Assert.False(vm.Sessions[0].IsSelected);
        Assert.True(vm.Sessions[1].IsSelected);
    }

    [Fact]
    public async Task SelectNextSession_WithASingleSession_StaysOnThatSession()
    {
        var vm = await NewVmWithSessionsAsync(1);
        var only = vm.Sessions[0];

        vm.SelectNextSession();
        vm.SelectPreviousSession();

        Assert.Equal(only, vm.SelectedSession);
        Assert.True(only.IsSelected);
    }

    [Fact]
    public void SelectNextSession_WithNoSessions_DoesNothing()
    {
        var vm = NewVm();

        vm.SelectNextSession();
        vm.SelectPreviousSession();

        Assert.Null(vm.SelectedSession);
        Assert.Empty(vm.Sessions);
    }

    [Fact]
    public async Task GridColumns_IsOneForZeroOrOneSessionAndTwoForMore()
    {
        var vm = NewVm();
        Assert.Equal(1, vm.GridColumns);

        await vm.NewSessionCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.GridColumns);

        await vm.NewSessionCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.GridColumns);

        await vm.NewSessionCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.GridColumns);
    }

    [Fact]
    public async Task SaveAllSettingsCommand_PersistsEverySectionAndReportsEachAsSaved()
    {
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());

        var vm = new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            DefaultDialogService(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore);

        await vm.SaveAllSettingsCommand.ExecuteAsync(null);

        await notificationSettingsStore.Received(1).SaveAsync(Arg.Any<NotificationSettings>(), Arg.Any<CancellationToken>());
        await transcriptDisplaySettingsStore.Received(1).SaveAsync(Arg.Any<TranscriptDisplaySettings>(), Arg.Any<CancellationToken>());
        await sessionBehaviorSettingsStore.Received(1).SaveAsync(Arg.Any<SessionBehaviorSettings>(), Arg.Any<CancellationToken>());
        await layoutSettingsStore.Received(1).SaveAsync(Arg.Any<LayoutSettings>(), Arg.Any<CancellationToken>());
        await voiceSettingsStore.Received(1).SaveAsync(Arg.Any<VoiceSettings>(), Arg.Any<CancellationToken>());
        await terminalSettingsStore.Received(1).SaveAsync(Arg.Any<TerminalSettings>(), Arg.Any<CancellationToken>());

        Assert.Equal("Saved", vm.NotificationSettingsStatus);
        Assert.Equal("Saved", vm.TranscriptDisplaySettingsStatus);
        Assert.Equal("Saved", vm.SessionBehaviorSettingsStatus);
        Assert.Equal("Saved", vm.LayoutSettingsStatus);
        Assert.Equal("Saved", vm.VoiceSettingsStatus);
        Assert.Equal("Saved", vm.TerminalSettingsStatus);
    }

    [Fact]
    public void Constructor_DefaultsSidebarWidthBeforeLayoutSettingsLoad()
    {
        var vm = NewVm();

        Assert.Equal(LayoutSettings.DefaultSidebarWidth, vm.SidebarWidth);
    }

    [Fact]
    public async Task Constructor_LoadsSidebarWidthFromLayoutSettingsStore()
    {
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings { SidebarWidth = 300 });

        var vm = NewVm(layoutSettingsStore: layoutSettingsStore);
        await Task.Delay(50);

        Assert.Equal(300, vm.SidebarWidth);
    }

    [Fact]
    public async Task SetSidebarWidthAsync_PersistsTheWidthAndUpdatesTheProperty()
    {
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var vm = NewVm(layoutSettingsStore: layoutSettingsStore);

        await vm.SetSidebarWidthAsync(320);

        Assert.Equal(320, vm.SidebarWidth);
        await layoutSettingsStore.Received(1).SaveAsync(
            Arg.Is<LayoutSettings>(s => s.SidebarWidth == 320), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(40, LayoutSettings.MinSidebarWidth)]
    [InlineData(2000, LayoutSettings.MaxSidebarWidth)]
    public async Task SetSidebarWidthAsync_ClampsAnOutOfRangeWidth(double requested, double expected)
    {
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var vm = NewVm(layoutSettingsStore: layoutSettingsStore);

        await vm.SetSidebarWidthAsync(requested);

        Assert.Equal(expected, vm.SidebarWidth);
        await layoutSettingsStore.Received(1).SaveAsync(
            Arg.Is<LayoutSettings>(s => s.SidebarWidth == expected), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Constructor_LoadsTerminalSettingsFromStore()
    {
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings { FontFamily = "JetBrains Mono", FontSize = 18 });

        var vm = NewVm(terminalSettingsStore: terminalSettingsStore);

        // The load runs fire-and-forget from the constructor (same pattern as the other settings
        // sections); give it a beat to complete before asserting.
        await Task.Delay(50);

        Assert.Equal("JetBrains Mono", vm.TerminalFontFamily);
        Assert.Equal(18, vm.TerminalFontSize);
    }

    [Fact]
    public async Task SaveTerminalSettingsCommand_ClampsFontSizeAndTrimsBlankFontFamilyToTheDefault()
    {
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());
        var vm = NewVm(terminalSettingsStore: terminalSettingsStore);
        vm.TerminalFontFamily = "   ";
        vm.TerminalFontSize = 999;

        await vm.SaveTerminalSettingsCommand.ExecuteAsync(null);

        await terminalSettingsStore.Received(1).SaveAsync(
            Arg.Is<TerminalSettings>(s => s.FontFamily == "Cascadia Mono, Consolas, monospace" && s.FontSize == TerminalSettings.MaxFontSize),
            Arg.Any<CancellationToken>());
        Assert.Equal("Cascadia Mono, Consolas, monospace", vm.TerminalFontFamily);
        Assert.Equal(TerminalSettings.MaxFontSize, vm.TerminalFontSize);
        Assert.Equal("Saved", vm.TerminalSettingsStatus);
    }

    [Fact]
    public async Task NewTtySession_IsSeededWithTheCurrentTerminalFontSettings()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Tty));
        var vm = NewVm(dialogService);
        vm.TerminalFontFamily = "Fira Code";
        vm.TerminalFontSize = 20;

        await vm.NewSessionCommand.ExecuteAsync(null);

        var tty = Assert.IsType<TtyViewModel>(vm.Sessions[0]);
        Assert.Equal("Fira Code", tty.TerminalFontFamily);
        Assert.Equal(20, tty.TerminalFontSize);
    }

    [Fact]
    public async Task ChangingTerminalFontSettings_PushesLiveToOpenTtySessions()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Tty));
        var vm = NewVm(dialogService);
        await vm.NewSessionCommand.ExecuteAsync(null);
        var tty = Assert.IsType<TtyViewModel>(vm.Sessions[0]);

        vm.TerminalFontFamily = "DejaVu Sans Mono";
        vm.TerminalFontSize = 24;

        Assert.Equal("DejaVu Sans Mono", tty.TerminalFontFamily);
        Assert.Equal(24, tty.TerminalFontSize);
    }

    [Fact]
    public async Task LoadingACuratedFont_SelectsItInTheDropdownWithoutCustomMode()
    {
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings { FontFamily = "JetBrains Mono", FontSize = 14 });

        var vm = NewVm(terminalSettingsStore: terminalSettingsStore);
        await Task.Delay(50);

        Assert.Equal("JetBrains Mono", vm.TerminalFontSelection);
        Assert.False(vm.IsTerminalFontCustom);
    }

    [Fact]
    public async Task LoadingAFontOutsideTheCuratedList_ReopensInCustomMode()
    {
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings { FontFamily = "Comic Mono", FontSize = 14 });

        var vm = NewVm(terminalSettingsStore: terminalSettingsStore);
        await Task.Delay(50);

        Assert.Equal(CockpitViewModel.CustomFontChoice, vm.TerminalFontSelection);
        Assert.True(vm.IsTerminalFontCustom);
        Assert.Equal("Comic Mono", vm.TerminalCustomFontFamily);
        Assert.Equal("Comic Mono", vm.TerminalFontFamily);
    }

    [Fact]
    public async Task NewTtySession_IsSeededWithTheCurrentVerticalLayoutSetting()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Tty));
        var vm = NewVm(dialogService);
        vm.GlobalStackSessionsVertically = true;

        await vm.NewSessionCommand.ExecuteAsync(null);

        var tty = Assert.IsType<TtyViewModel>(vm.Sessions[0]);
        Assert.True(tty.IsVerticalLayout);
    }

    [Fact]
    public async Task ChangingStackSessionsVertically_PushesLiveToOpenTtySessions()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Tty));
        var vm = NewVm(dialogService);
        await vm.NewSessionCommand.ExecuteAsync(null);
        var tty = Assert.IsType<TtyViewModel>(vm.Sessions[0]);

        vm.GlobalStackSessionsVertically = true;

        Assert.True(tty.IsVerticalLayout);
    }

    [Fact]
    public void ChoosingCustomThenTypingAFont_DrivesTheEffectiveFontFamily()
    {
        var vm = NewVm();

        vm.TerminalFontSelection = CockpitViewModel.CustomFontChoice;
        Assert.True(vm.IsTerminalFontCustom);

        vm.TerminalCustomFontFamily = "Comic Mono, monospace";

        Assert.Equal("Comic Mono, monospace", vm.TerminalFontFamily);
    }

    [Fact]
    public void SwitchingFromCustomBackToACuratedFont_UsesThatFontAndLeavesCustomMode()
    {
        var vm = NewVm();
        vm.TerminalFontSelection = CockpitViewModel.CustomFontChoice;
        vm.TerminalCustomFontFamily = "Comic Mono";

        vm.TerminalFontSelection = "Consolas";

        Assert.False(vm.IsTerminalFontCustom);
        Assert.Equal("Consolas", vm.TerminalFontFamily);
    }

    // #52: a settings-Save should let a plugin's already-built contributions (e.g. a side-menu section that
    // fetched data once at construction) refresh without an app restart. CockpitViewModel is the
    // IPluginContributionSink every CockpitHost is built against, so it's the seam that routes a save for one
    // plugin's id to only that plugin's registered callbacks.
    [Fact]
    public void SettingsSaved_RunsOnlyTheHandlersRegisteredForThatPluginId()
    {
        var vm = NewVm();
        var sink = (IPluginContributionSink)vm;
        var prCalls = 0;
        var youTrackCalls = 0;
        sink.AddSettingsSavedHandler("github-pull-requests", () => prCalls++);
        sink.AddSettingsSavedHandler("youtrack", () => youTrackCalls++);

        sink.NotifySettingsSaved("github-pull-requests");

        Assert.Equal(1, prCalls);
        Assert.Equal(0, youTrackCalls);
    }

    [Fact]
    public void SettingsSaved_RunsEveryHandlerRegisteredForThatPluginId()
    {
        var vm = NewVm();
        var sink = (IPluginContributionSink)vm;
        var firstCalls = 0;
        var secondCalls = 0;
        sink.AddSettingsSavedHandler("youtrack", () => firstCalls++);
        sink.AddSettingsSavedHandler("youtrack", () => secondCalls++);

        sink.NotifySettingsSaved("youtrack");

        Assert.Equal(1, firstCalls);
        Assert.Equal(1, secondCalls);
    }

    [Fact]
    public void SettingsSaved_WithNoHandlersRegistered_DoesNotThrow()
    {
        var vm = NewVm();
        var sink = (IPluginContributionSink)vm;

        var act = () => sink.NotifySettingsSaved("no-such-plugin");

        act();
    }

    // Settings are now reachable from several places (the manager's gear, the gear on a plugin's left-menu entry
    // or dialog, and the plugin itself), and every one of them opens the same dialog through this one seam —
    // titled after the plugin, whichever gear was pressed.
    [Fact]
    public async Task OpenPluginSettings_OpensThePluginsOwnViewTitledAfterIt()
    {
        var dialogHost = Substitute.For<IPluginDialogHost>();
        var vm = NewVm(pluginDialogHost: dialogHost);
        var view = new TextBlock();
        ((IPluginContributionSink)vm).AddPluginSettings("youtrack", "YouTrack", () => view);

        await vm.OpenPluginSettingsAsync("youtrack");

        await dialogHost.Received(1).ShowSettingsDialogAsync(
            "YouTrack settings",
            Arg.Any<Func<Control>>(),
            Arg.Any<double>(),
            Arg.Any<double>(),
            Arg.Any<Action?>(),
            // Keyed on the plugin, not merely keyed (AC-367): both gears route here, and since these windows stopped
            // being modal two forms could otherwise stand open over one store with the last save winning silently.
            "settings:youtrack");
    }

    // Saving from any gear must run the plugin's settings-saved handlers: a plugin that re-registers its MCP
    // server on save cannot depend on which one the operator reached for.
    [Fact]
    public async Task SavingFromAnyGear_RunsThePluginsSettingsSavedHandlers()
    {
        var dialogHost = Substitute.For<IPluginDialogHost>();
        dialogHost
            .ShowSettingsDialogAsync(Arg.Any<string>(), Arg.Any<Func<Control>>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Action?>(), Arg.Any<string?>())
            .Returns(callInfo =>
            {
                callInfo.Arg<Action?>()?.Invoke();
                return Task.CompletedTask;
            });
        var vm = NewVm(pluginDialogHost: dialogHost);
        var sink = (IPluginContributionSink)vm;
        var saves = 0;
        sink.AddPluginSettings("youtrack", "YouTrack", () => new TextBlock());
        sink.AddSettingsSavedHandler("youtrack", () => saves++);

        await vm.OpenPluginSettingsAsync("youtrack");

        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task OpenPluginSettings_ForAPluginThatRegisteredNone_DoesNothing()
    {
        var dialogHost = Substitute.For<IPluginDialogHost>();
        var vm = NewVm(pluginDialogHost: dialogHost);

        await vm.OpenPluginSettingsAsync("youtrack");

        Assert.False(vm.HasPluginSettings("youtrack"));
        await dialogHost.DidNotReceiveWithAnyArgs().ShowSettingsDialogAsync(default!, default!, default, default, default);
    }

    private static async Task<CockpitViewModel> NewVmWithSessionsAsync(int count)
    {
        var vm = NewVm();
        for (var i = 0; i < count; i++)
        {
            await vm.NewSessionCommand.ExecuteAsync(null);
        }

        return vm;
    }

    [Fact]
    public async Task OpenPluginStoreUpdatesAsync_OpensTheStoreDialogWithTheAvailableUpdatesFilterPreselected()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        var vm = NewVm(dialogService);

        await vm.OpenPluginStoreUpdatesAsync();

        await dialogService.Received(1).ShowPluginStoreDialogAsync(
            Arg.Is<PluginManagerViewModel>(manager => manager == vm.Plugins),
            PluginStoreFilter.UpdatesAvailable);
    }

    [Fact]
    public void ActiveShortcuts_KeepTheSessionManagementActionsLiveOverTheTerminal_ButNotTheDialogOpeners()
    {
        var vm = NewVm();

        var previousSession = vm.ActiveShortcuts.Single(binding => binding.Label == "Previous session");
        var nextSession = vm.ActiveShortcuts.Single(binding => binding.Label == "Next session");
        var newSession = vm.ActiveShortcuts.Single(binding => binding.Label == "New session");
        var duplicateSession = vm.ActiveShortcuts.Single(binding => binding.Label == "Duplicate active session");
        var manageProfiles = vm.ActiveShortcuts.Single(binding => binding.Label == "Manage profiles");

        Assert.Equal("Ctrl+Shift+Up", previousSession.Gesture);
        Assert.True(previousSession.ActiveInTerminal);
        Assert.Equal("Ctrl+Shift+Down", nextSession.Gesture);
        Assert.True(nextSession.ActiveInTerminal);

        // Session-management actions fire over a focused terminal (Raymond's call).
        Assert.True(newSession.ActiveInTerminal);
        Assert.True(duplicateSession.ActiveInTerminal);

        // A dialog-opener on a single-key shell gesture (Ctrl+R) stays gated so it reaches the shell.
        Assert.False(manageProfiles.ActiveInTerminal);
    }

    [Fact]
    public async Task SelectNextSessionCommand_MovesTheSelectionAndWraps()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.NewSessionCommand.ExecuteAsync(null);
        vm.SelectSessionCommand.Execute(vm.Sessions[0]);

        vm.SelectNextSessionCommand.Execute(null);
        Assert.Equal(vm.Sessions[1], vm.SelectedSession);

        vm.SelectNextSessionCommand.Execute(null);
        Assert.Equal(vm.Sessions[0], vm.SelectedSession);

        vm.SelectPreviousSessionCommand.Execute(null);
        Assert.Equal(vm.Sessions[1], vm.SelectedSession);
    }

    [Fact]
    public async Task ClosingASession_ReleasesEveryCouplingItDrove()
    {
        // AC-834: the diagram and whiteboard registries are the same shape as the terminal one but were never
        // released here, so a coupled surface kept an "agent connected" bar for an agent that was gone — and held
        // the surface against every other session, since IsCoupledByAnother refuses a second one.
        var terminals = Substitute.For<ITerminalAccessRegistry>();
        var diagrams = Substitute.For<IDiagramAccessRegistry>();
        var whiteboards = Substitute.For<IWhiteboardAccessRegistry>();
        var vm = NewVm(terminals: terminals, diagrams: diagrams, whiteboards: whiteboards);
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];

        await vm.ConfirmCloseSessionCommand.ExecuteAsync(session);

        terminals.Received(1).SessionEnded(session.PaneId);
        diagrams.Received(1).SessionEnded(session.PaneId);
        whiteboards.Received(1).SessionEnded(session.PaneId);
    }

    // AC-692/AC-700: drives the real `SampleResources`/`ResourceMonitor` wire over a fake process table, and demands
    // both notices on one crossing — either one alone is a regression. The decision logic itself is
    // `SessionMemoryPressureTests`.
    [Fact]
    public async Task ASessionOverItsCap_GetsBothANamedToast_AndAKillOnItsOwnBar()
    {
        const long Megabyte = 1024 * 1024;
        var reader = Substitute.For<IProcessTableReader>();
        reader.Read().Returns([new ProcessRow(4242, 1, TimeSpan.Zero, 5000 * Megabyte)]);

        var vm = NewVm(resourceMonitor: new ResourceMonitor(reader));
        var session = new TtyViewModel { Title = "leaky-build", ProcessId = 4242, MemoryCapBytes = 4096 * Megabyte };
        vm.Sessions.Add(session);

        vm.SampleResources();

        var toast = Assert.Single(vm.Toasts);
        Assert.Contains("leaky-build", toast.Message, StringComparison.Ordinal);
        Assert.Equal("Kill", toast.ActionLabel);

        Assert.True(session.IsOverMemoryCap);
        Assert.Contains("over its", session.UsageWarning, StringComparison.Ordinal);

        // The bar's own Kill takes the ordinary self-close path; that `CloseRequested` tears the session down is
        // covered where the cockpit's close wiring is. The toast's Kill goes straight through the command.
        var askedFromTheBar = false;
        session.CloseRequested += (_, _) => askedFromTheBar = true;
        session.KillOverCapSessionCommand.Execute(null);
        Assert.True(askedFromTheBar);

        toast.InvokeActionCommand.Execute(null);
        await Task.Delay(10);

        Assert.DoesNotContain(session, vm.Sessions);
    }

    // The bar outlives the toast: a toast is gone in seconds, and the operator who was looking elsewhere must still
    // find the choice on the pane. Nothing takes the bar down but the operator (AC-700).
    [Fact]
    public void TheOverCapBar_StaysUpAcrossLaterSamples_UntilItIsDismissed()
    {
        const long Megabyte = 1024 * 1024;
        var reader = Substitute.For<IProcessTableReader>();
        reader.Read().Returns([new ProcessRow(4242, 1, TimeSpan.Zero, 5000 * Megabyte)]);

        var vm = NewVm(resourceMonitor: new ResourceMonitor(reader));
        var session = new TtyViewModel { Title = "leaky-build", ProcessId = 4242, MemoryCapBytes = 4096 * Megabyte };
        vm.Sessions.Add(session);

        vm.SampleResources();
        vm.SampleResources();
        vm.SampleResources();

        // One toast for one crossing — the bar standing does not re-announce it — and the bar still up three
        // samples later.
        Assert.Single(vm.Toasts);
        Assert.True(session.HasUsageWarning);

        session.DismissUsageWarningCommand.Execute(null);
        vm.SampleResources();

        Assert.False(session.HasUsageWarning);
    }

    // AC-734: the assistant's own tool-server process is a direct child of the cockpit that is not in `Sessions`
    // (see `CreateAssistantSession`), so it lands in `usage.Parts.Children` labeled with the raw process name —
    // "claude" — same as any other MCP tool server. Matched here by process id instead.
    [Fact]
    public void TheAssistantsOwnProcess_IsLabelledAssistant_NotTheBareProcessName()
    {
        const long Megabyte = 1024 * 1024;
        var reader = Substitute.For<IProcessTableReader>();
        reader.Read().Returns([
            new ProcessRow(4242, Environment.ProcessId, TimeSpan.Zero, 689 * Megabyte, "claude"),
        ]);

        var vm = NewVm(resourceMonitor: new ResourceMonitor(reader));
        var assistant = vm.CreateAssistantSession("assistant");
        Assert.NotNull(assistant);
        assistant!.ProcessId = 4242;

        vm.SampleResources();

        var row = Assert.Single(vm.ResourceRows, row => row.Memory == "689 MB");
        Assert.Equal("Assistant", row.Title);
    }

    // A second, unrelated process that happens to share the literal name "claude" merges with the assistant's own
    // into one grouped line (`CockpitBreakdown`'s existing dedup) — and a merged line carries no single process id
    // to trust, so it is left on the generic name rather than guessed at.
    [Fact]
    public void ASecondUnrelatedClaudeProcess_LeavesTheRowOnItsGenericName()
    {
        const long Megabyte = 1024 * 1024;
        var reader = Substitute.For<IProcessTableReader>();
        reader.Read().Returns([
            new ProcessRow(4242, Environment.ProcessId, TimeSpan.Zero, 689 * Megabyte, "claude"),
            new ProcessRow(4343, Environment.ProcessId, TimeSpan.Zero, 50 * Megabyte, "claude"),
        ]);

        var vm = NewVm(resourceMonitor: new ResourceMonitor(reader));
        var assistant = vm.CreateAssistantSession("assistant");
        Assert.NotNull(assistant);
        assistant!.ProcessId = 4242;

        vm.SampleResources();

        Assert.DoesNotContain(vm.ResourceRows, row => row.Title == "Assistant");
        Assert.Contains(vm.ResourceRows, row => row.Title == "claude ×2");
    }

    private static CockpitViewModel NewVm(
        ISessionDialogService? dialogService = null,
        ITerminalSettingsStore? terminalSettingsStore = null,
        ILayoutSettingsStore? layoutSettingsStore = null,
        IPluginDialogHost? pluginDialogHost = null,
        ITerminalAccessRegistry? terminals = null,
        IDiagramAccessRegistry? diagrams = null,
        IWhiteboardAccessRegistry? whiteboards = null,
        ProjectsViewModel? projects = null,
        IFirstRunWizard? firstRunWizard = null,
        Func<string, bool>? tryOpenExternalLink = null,
        ResourceMonitor? resourceMonitor = null)
    {
        var captureService = Substitute.For<IAudioCaptureService>();
        var playbackService = Substitute.For<IAudioPlaybackService>();
        var attentionNotifier = Substitute.For<IAttentionNotifier>();
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        if (layoutSettingsStore is null)
        {
            layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
            layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        }

        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        if (terminalSettingsStore is null)
        {
            terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
            terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());
        }

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            dialogService ?? DefaultDialogService(),
            captureService,
            playbackService,
            attentionNotifier,
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore,
            pluginDialogHost: pluginDialogHost,
            terminals: terminals,
            diagrams: diagrams,
            whiteboards: whiteboards,
            projects: projects,
            firstRunWizard: firstRunWizard,
            tryOpenExternalLink: tryOpenExternalLink,
            resourceMonitor: resourceMonitor);
    }

    /// <summary>A projects view model over a store holding exactly <paramref name="saved"/>, already loaded.</summary>
    private static async Task<ProjectsViewModel> LoadedProjectsAsync(params Project[] saved)
    {
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync().Returns(new ProjectSettings { Projects = saved });
        var projects = new ProjectsViewModel(store, dialogs: null);
        await projects.LoadAsync();
        return projects;
    }

    private static Project TrackedIn(string name, string trackerProject) =>
        new(name.ToLowerInvariant(), name)
        {
            PluginFields = new Dictionary<string, string> { ["youtrack.project"] = trackerProject },
        };

    private static ISessionDialogService DefaultDialogService()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Sdk));
        return dialogService;
    }

    private static NewSessionResult NewSessionResultFor(SessionKind kind) => new(
        kind,
        new SessionProfile("default", new ClaudeConfig(@"C:\fake\.claude")),
        SessionOptionCatalog.DefaultPermissionMode,
        SessionOptionCatalog.DefaultModel,
        SessionOptionCatalog.DefaultEffort, null);
}
