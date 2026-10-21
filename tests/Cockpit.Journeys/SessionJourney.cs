using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.HeavyRuns;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Worktrees;
using Cockpit.Plugins.Abstractions.StatusBar;
using Cockpit.TestSupport;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cockpit.Journeys;

// The product's core act on the desktop: a session started from a project and a profile, a prompt through its
// composer and the answer in its pane. The route the operator takes, through the real New session dialog.
[Collection(JourneyCollection.Alone)]
public sealed class SessionJourney
{
    // J2: the dialog opens the pane, the answer lands busy then done (AC-1437); Stop is no failure (AC-1031, AC-1449), a
    // failed turn offers Retry from the event log (AC-728, AC-1438). AC-1535: four heavy runs on a loaded machine — one
    // runs, three wait with a reason, a closed session's place comes back, and the pre-merge gate starts first.
    [Fact]
    public async Task ASessionStartedFromAProjectAndAProfile_AnswersInItsPane()
    {
        var load = new FakeMachineLoad();
        await using var cockpit = JourneyHost.Desktop(configure: services => services.AddSingleton<IMachineResources>(load));
        var folder = Directory.CreateDirectory(Path.Combine(cockpit.StateRoot, "project")).FullName;
        var project = await cockpit.SaveProfileAndProjectAsync(folder);
        await cockpit.StartDesktopAsync();

        var session = await cockpit.StartSessionThroughTheDialogAsync(project);
        var statuses = new List<SessionStatus>();
        TranscriptEntryViewModel? failed = null;
        string? afterStop = null;
        var failedAfterStop = true;
        await HeadlessAvalonia.RunAsync(async () =>
        {
            session.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(session.SessionStatus))
                {
                    statuses.Add(session.SessionStatus);
                }
            };
            session.InputText = "hello";
            await session.SendCommand.ExecuteAsync(null);
            await Until.CollectionHolds(session.Transcript, () => session.Transcript.Any(row => row.Kind == TranscriptEntryKind.AssistantText && row.Text == "echo: hello"));
            await Until.Holds(session, () => session.SessionStatus == SessionStatus.Done);

            session.InputText = EchoDriver.WaitingPrompt;
            await session.SendCommand.ExecuteAsync(null);
            await session.StopCommand.ExecuteAsync(null);
            afterStop = session.Status;
            await Until.Holds(session, () => !session.IsBusy);
            failedAfterStop = session.Transcript.Any(row => row.IsFailedTurnRow);

            session.InputText = EchoDriver.FailingPrompt;
            await session.SendCommand.ExecuteAsync(null);
            await Until.Holds(session, () => (failed = session.Transcript.LastOrDefault(row => row.IsFailedTurnRow))?.ActionLabel == "Retry");
        });

        Assert.Equal(JourneyHost.EchoProfile, cockpit.Driver.Profile?.Label);
        Assert.Equal([SessionStatus.Busy, SessionStatus.Done], statuses.Take(2));
        Assert.Equal("Interrupted.", afterStop);
        Assert.False(failedAfterStop);
        Assert.NotNull(failed);
        Assert.Contains(session, HeadlessAvalonia.Run(() => cockpit.Cockpit.Sessions.ToList()));

        var other = await cockpit.StartSessionThroughTheDialogAsync(project);
        var endpoint = cockpit.Services.GetServices<ICockpitInternalMcpProvider>().SelectMany(provider => provider.GetServers())
            .Single(server => server.Name == "cockpit-session").Url ?? throw new InvalidOperationException("No session endpoint.");
        await using var holder = await cockpit.ConnectAsPaneAsync(other.PaneId, "cockpit-session", endpoint);
        await using var own = await cockpit.ConnectAsPaneAsync(session.PaneId, "cockpit-session", endpoint);
        async Task<string> StartAsync(McpClient client, bool isQueued, string purpose, string command, params string[] arguments)
        {
            var started = await JourneyHost.CallAsync(client, "start_run", new()
            {
                ["workingDirectory"] = folder,
                ["command"] = command,
                ["arguments"] = arguments,
                ["kind"] = "journeys",
                ["purpose"] = purpose,
            });
            Assert.Equal(isQueued, started["queued"]?.GetValue<bool>() ?? false);
            return started["runId"]?.GetValue<string>() ?? throw new InvalidOperationException("No run id.");
        }

        _ = OperatingSystem.IsWindows()
            ? await StartAsync(holder, false, "explore", "powershell", "-NoProfile", "-Command", "Start-Sleep -Seconds 120")
            : await StartAsync(holder, false, "explore", "sleep", "120");
        string[] queued =
        [
            await StartAsync(own, true, "explore", "git", "--version"),
            await StartAsync(own, true, "explore", "git", "--version"),
            await StartAsync(own, true, "pre-merge", "git", "--version"),
        ];
        var heavy = cockpit.Services.GetServices<ISupervisedActivitySource>().Single(source => source.Label == "Heavy runs");
        var waiting = heavy.Snapshot().Where(run => run.Details.Any(detail => detail.Label == "Waiting")).ToList();
        Assert.Equal([queued[2], queued[0], queued[1]], waiting.Select(run => run.Id));
        Assert.All(waiting, run => Assert.StartsWith("waiting: CPU 85%", run.Details.Single(detail => detail.Label == "Waiting").Value));

        await HeadlessAvalonia.RunAsync(() => cockpit.Cockpit.CloseSessionCommand.ExecuteAsync(other));
        load.Set(40);

        // The verdicts reach the session's inbox, which the cockpit attaches to its next tool result, in the order the runs ended.
        List<(string RunId, string Verdict)> verdicts = [];
        await Until.ReloadHolds(
            async () =>
            {
                var result = await own.CallToolAsync("run_status", new Dictionary<string, object?> { ["runId"] = queued[0] });
                var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
                verdicts.AddRange(Regex.Matches(text, @"Run (\w+) \('[^']*'\) finished: (\w+)").Select(match => (match.Groups[1].Value, match.Groups[2].Value)));
            },
            () => queued.All(runId => verdicts.Any(verdict => verdict.RunId == runId)));
        Assert.Equal(queued[2], verdicts[0].RunId);
        Assert.All(verdicts, verdict => Assert.Equal("Passed", verdict.Verdict));
    }

    // J4: a closed conversation is still in the index after a restart, and reopens as one pane resuming it with its rows;
    // reopened again while open, it is selected rather than started twice (AC-1531).
    [Fact]
    public async Task AClosedConversation_IsInTheIndexAfterARestart_AndReopensResumedInOnePane()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"journey-conversation-{Guid.NewGuid():N}")).FullName;
        try
        {
            Project project;
            string title;
            await using (var before = JourneyHost.Desktop(stateRoot: root))
            {
                project = await before.SaveProfileAndProjectAsync(Directory.CreateDirectory(Path.Combine(root, "project")).FullName);
                await before.StartDesktopAsync();
                var session = await before.StartSessionThroughTheDialogAsync(project);
                title = session.Title;
                await HeadlessAvalonia.RunAsync(async () =>
                {
                    session.InputText = "history";
                    await session.SendCommand.ExecuteAsync(null);
                    await Until.CollectionHolds(session.Transcript, () => session.Transcript.Any(row => row.Text == "echo: history"));
                });
                await HeadlessAvalonia.RunAsync(() => before.Cockpit.CloseSessionCommand.ExecuteAsync(session));
            }

            await using var after = JourneyHost.Desktop(stateRoot: root);
            await after.StartDesktopAsync();
            var record = Assert.Single(await after.Services.GetRequiredService<IConversationIndex>().LoadAsync());
            Assert.Equal(
                (title, project.Id, JourneyHost.EchoProfile, "claude", "echo-conversation", SessionConversationIdState.Known, "Done"),
                (record.Title, record.ProjectId, record.ProfileId, record.ProviderId, record.ConversationId, record.ConversationState, record.LastStatus));
            Assert.True(record.ClosedAt >= record.StartedAt);
            async Task<bool> ReopenAsync()
            {
                var done = false;
                await HeadlessAvalonia.RunAsync(async () => done = (await after.Cockpit.ReopenConversationAsync(record)).Reopened);
                return done;
            }

            HeadlessAvalonia.Run(() => after.Cockpit.SimpleView = true);
            Assert.True(await ReopenAsync());
            var reopened = Assert.IsType<SessionViewModel>(Assert.Single(HeadlessAvalonia.Run(() => after.Cockpit.Sessions.ToList())));
            Assert.Equal((reopened, SimplePage.Conversation), HeadlessAvalonia.Run(() => (after.Cockpit.SimpleSelectedSession, after.Cockpit.SimplePage)));
            Assert.Equal((project.Id, JourneyHost.EchoProfile), (reopened.ProjectId, reopened.ActiveProfileLabel));
            Assert.Equal(SessionResume.BySessionId("echo-conversation"), Assert.Single(after.Drivers.Made).Resume);
            await HeadlessAvalonia.RunAsync(() => Until.CollectionHolds(reopened.Transcript, () => reopened.Transcript.Any(row => row.Text == "echo: history")));

            HeadlessAvalonia.Run(() => (after.Cockpit.SimpleView, after.Cockpit.SelectedSession) = (false, null));
            Assert.True(await ReopenAsync());
            Assert.Same(reopened, Assert.Single(HeadlessAvalonia.Run(() => after.Cockpit.Sessions.ToList())));
            Assert.Same(reopened, HeadlessAvalonia.Run(() => after.Cockpit.SelectedSession));

            HeadlessAvalonia.Run(() => after.Cockpit.SimpleView = true);
            HeadlessAvalonia.Run(() => after.Cockpit.ShowSimpleStartScreenCommand.Execute(null));
            Assert.Equal(SimplePage.Home, HeadlessAvalonia.Run(() => after.Cockpit.SimplePage));
            Assert.True(await ReopenAsync());
            Assert.Equal((reopened, SimplePage.Conversation), HeadlessAvalonia.Run(() => (after.Cockpit.SimpleSelectedSession, after.Cockpit.SimplePage)));
        }
        finally
        {
            JourneyHost.RemoveStateRoot(root);
        }
    }

    // J3: in a project that is a git repository and isolates by default, the session runs in a worktree on a branch
    // of its own, and closing the pane takes both away again.
    [Fact]
    public async Task AWorktreeSession_GetsItsOwnWorktree_AndClosingThePaneRemovesItAndItsBranch()
    {
        using var fixture = new CleanupFixture();
        await fixture.InitializeAsync();
        await using var cockpit = JourneyHost.Desktop(configure: services =>
        {
            services.AddSingleton<IWorktreeManager>(fixture.Manager);
            services.AddSingleton<IWorktreeRegistry>(fixture.Registry);
            services.AddSingleton<IDockerCli>(fixture.Docker);
        });
        var repository = fixture.Repository;
        var project = await cockpit.SaveProfileAndProjectAsync(repository, isolateInWorktree: true);
        await cockpit.StartDesktopAsync();

        var session = await cockpit.StartSessionThroughTheDialogAsync(project);
        var worktree = (await fixture.Manager.ListAsync()).Single(record => record.SessionId == session.PaneId);
        var branchWhileOpen = await _BranchesAsync(repository, worktree.Branch);
        var unpushed = await cockpit.StartSessionThroughTheDialogAsync(project);
        var alive = await cockpit.StartSessionThroughTheDialogAsync(project);
        var liveWorktree = (await fixture.Manager.ListAsync()).Single(record => record.SessionId == alive.PaneId);
        var live = cockpit.Services.GetRequiredService<ILiveSessionRegistry>();
        var endpoint = cockpit.Services.GetServices<ICockpitInternalMcpProvider>().SelectMany(provider => provider.GetServers())
            .Single(server => server.Name == "cockpit-worktrees").Url ?? throw new InvalidOperationException("No worktree endpoint.");
        async Task<string> GateAsync(string pane, string branch)
        {
            await using var client = await cockpit.ConnectAsPaneAsync(pane, "cockpit-worktrees", endpoint);
            var result = await JourneyHost.CallAsync(client, "worktree_create", new()
            {
                ["session"] = pane,
                ["directory"] = repository,
                ["branch"] = branch,
            });
            Assert.True(result["ok"]?.GetValue<bool>());
            return result["path"]?.GetValue<string>() ?? throw new InvalidOperationException("No worktree path.");
        }

        var gate = await GateAsync(session.PaneId, "gate");
        var keep = await GateAsync(unpushed.PaneId, "keep");
        var dirty = await GateAsync(unpushed.PaneId, "dirty");
        await File.WriteAllTextAsync(Path.Combine(dirty, "unsaved.txt"), "keep this work");
        foreach (var path in new[] { gate, keep })
        {
            await File.WriteAllTextAsync(Path.Combine(path, "work.txt"), Path.GetFileName(path));
            await CleanupFixture.GitAsync(path, "add", ".");
            await CleanupFixture.GitAsync(path, "commit", "-m", "session work");
        }

        var temp = Directory.CreateDirectory(Path.Combine(fixture.TempRoot, session.PaneId)).FullName;
        await File.WriteAllTextAsync(Path.Combine(temp, "test.txt"), "temporary");
        var liveTemp = Directory.CreateDirectory(Path.Combine(fixture.TempRoot, alive.PaneId)).FullName;
        await File.WriteAllTextAsync(Path.Combine(liveTemp, "live.txt"), "still running");
        fixture.Docker.Add("orphan", "postgres:17", new() { ["org.testcontainers"] = "true", ["org.testcontainers.session-id"] = "gone" });
        fixture.Docker.Add("live", "app", new() { ["com.docker.compose.project"] = "live", ["com.docker.compose.project.working_dir"] = liveTemp });
        await HeadlessAvalonia.RunAsync(async () =>
        {
            await cockpit.Cockpit.CloseSessionCommand.ExecuteAsync(session);
            await cockpit.Cockpit.CloseSessionCommand.ExecuteAsync(unpushed);
        });

        Assert.Equal(worktree.Path, cockpit.Driver.WorkingDirectory);
        Assert.NotEqual(Path.GetFullPath(repository), Path.GetFullPath(worktree.Path));
        Assert.NotEmpty(branchWhileOpen);
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Empty(await _BranchesAsync(repository, worktree.Branch));
        Assert.All((await fixture.Registry.ListAsync()).Where(record => record.SessionId != alive.PaneId), record => Assert.True(record.IsRetained));
        await CleanupFixture.GitAsync(gate, "push", "origin", "HEAD:gate");
        await CleanupFixture.GitAsync(repository, "update-ref", "-d", "refs/remotes/origin/gate");
        Assert.True(Directory.Exists(gate));

        using var reconciler = new WorktreeReconciler(fixture.Manager) { LiveSessionIds = () => live.LiveSessionIds.ToArray() };
        await reconciler.RunOnceAsync();

        Assert.False(Directory.Exists(gate));
        Assert.False(Directory.Exists(temp));
        Assert.Contains("orphan", fixture.Docker.Removed);
        Assert.True(Directory.Exists(keep));
        Assert.True(Directory.Exists(liveWorktree.Path));
        Assert.True(File.Exists(Path.Combine(liveTemp, "live.txt")));
        Assert.Equal("keep this work", await File.ReadAllTextAsync(Path.Combine(dirty, "unsaved.txt")));
        Assert.DoesNotContain("live", fixture.Docker.Removed);
        var records = await fixture.Registry.ListAsync();
        var retained = Assert.Single(records, record => record.Path == keep);
        Assert.Equal("1 commit not on the remote", retained.RetentionReason);
        Assert.Equal("1 file not committed", Assert.Single(records, record => record.Path == dirty).RetentionReason);
        await using var observer = await cockpit.ConnectAsPaneAsync(alive.PaneId, "cockpit-worktrees", endpoint);
        var listing = await JourneyHost.CallAsync(observer, "worktree_list", new());
        var reported = listing["worktrees"]?.AsArray().Single(row => row?["path"]?.GetValue<string>() == keep);
        Assert.Equal(retained.RetentionReason, reported?["retentionReason"]?.GetValue<string>());
        Assert.Equal("session work", await CleanupFixture.GitAsync(keep, "log", "-1", "--format=%s"));
    }

    // A repository with one commit, the least a worktree can branch from.
    // AC-1529 criteria 1 and 2: picked in the Simple rail, the conversation reads "project › title" with its stand in
    // plain words over a column of at most 720px; the header's Stop breaks the turn off and leaves Idle behind.
    [Fact]
    public async Task InTheSimpleStand_AConversationShowsWhereItIsAndHowItStands_AndItsStopEndsTheTurn()
    {
        await using var cockpit = JourneyHost.Desktop();
        var project = await cockpit.SaveProfileAndProjectAsync(Directory.CreateDirectory(Path.Combine(cockpit.StateRoot, "project")).FullName);
        await cockpit.StartDesktopAsync();
        var session = await cockpit.StartSessionThroughTheDialogAsync(project);

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = cockpit.Window;
            window.Width = 1280;
            window.Height = 800;
            cockpit.Cockpit.ShowSimpleViewCommand.Execute(null);
            cockpit.Cockpit.SelectSimpleSessionCommand.Execute(session);
            window.UpdateLayout();

            var header = window.GetVisualDescendants().OfType<SimpleConversationHeader>().Single(h => h.IsEffectivelyVisible);
            string?[] Shown(Control control) => control.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray();
            Assert.Same(session, header.DataContext);
            Assert.Contains(project.Name, Shown(header));
            Assert.Contains(session.Title, Shown(header));
            session.InputText = "hello";
            await session.SendCommand.ExecuteAsync(null);
            await Until.Holds(session, () => session.SessionStatus == SessionStatus.Done);
            Assert.Contains("Done", Shown(header));
            window.UpdateLayout();
            var column = window.GetVisualDescendants().OfType<SessionView>().Single(v => v.IsEffectivelyVisible)
                .GetVisualDescendants().OfType<Control>().Where(c => c.Name is "TranscriptScroll" or "ComposerBorder").ToList();
            Assert.Equal(2, column.Count);
            Assert.All(column, part => Assert.InRange(part.Bounds.Width, 1, 720));

            var stop = header.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SimpleStopButton");
            session.InputText = EchoDriver.WaitingPrompt;
            await session.SendCommand.ExecuteAsync(null);
            await Until.Holds(session, () => session.SessionStatus == SessionStatus.Busy);
            window.UpdateLayout();
            Assert.Contains("Busy", Shown(header));
            Assert.True(stop.IsEffectivelyVisible);

            stop.Command!.Execute(stop.CommandParameter);
            await Until.Holds(session, () => !session.IsBusy && session.SessionStatus == SessionStatus.Idle);
            window.UpdateLayout();
            Assert.Contains("Idle", Shown(header));
            Assert.False(stop.IsEffectivelyVisible);
        });
    }

    // AC-1530 criteria 1-3: in the Simple stand three reads in one folder are one step whose folder opens, the header
    // says which file a running Write is creating, and the turn ends in a green block of the three files it made, the
    // failed fourth left out; each file and Open folder open through the pane. A turn that wrote nothing has no block.
    [Fact]
    public async Task InTheSimpleStand_ATurnReadsAsSteps_AndEndsWithTheFilesItMade()
    {
        await using var cockpit = JourneyHost.Desktop();
        var project = await cockpit.SaveProfileAndProjectAsync(Directory.CreateDirectory(Path.Combine(cockpit.StateRoot, "project")).FullName);
        await cockpit.StartDesktopAsync();
        var session = await cockpit.StartSessionThroughTheDialogAsync(project);
        var folder = cockpit.Driver.WorkingDirectory!;
        var opened = new List<string>();

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = cockpit.Window;
            window.Width = 1280;
            window.Height = 800;
            session.OpenPath = path =>
            {
                opened.Add(path);
                return true;
            };
            cockpit.Cockpit.ShowSimpleViewCommand.Execute(null);
            cockpit.Cockpit.SelectSimpleSessionCommand.Execute(session);
            session.InputText = "hello";
            await session.SendCommand.ExecuteAsync(null);
            await Until.Holds(session, () => session.SessionStatus == SessionStatus.Done);
            Assert.DoesNotContain(session.Transcript, row => row.TurnResult is not null);

            session.InputText = EchoDriver.WorkingPrompt;
            await session.SendCommand.ExecuteAsync(null);
            await Until.Holds(session, () => session.SimpleStatusLabel == "Busy · Creating Kwartaaloverzicht-Q3.xlsx…");
            window.UpdateLayout();
            var view = window.GetVisualDescendants().OfType<SessionView>().Single(v => v.IsEffectivelyVisible);
            var steps = view.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Name == "PlainStepLine" && p.IsEffectivelyVisible).ToList();
            Assert.Equal(
                ["✓ Read 3 files in Facturen › 2026-Q3", "◷ Creating Kwartaaloverzicht-Q3.xlsx in Rapportages › 2026 …"],
                steps.Select(_Spoken));
            var stepFolder = steps[0].GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StepFolderButton");
            stepFolder.Command!.Execute(stepFolder.CommandParameter);
            Assert.Equal([Path.Combine(folder, "Facturen", "2026-Q3")], opened);

            cockpit.Driver.FinishWriting();
            await Until.Holds(session, () => session.SessionStatus == SessionStatus.Done);
            window.UpdateLayout();
            var block = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TurnResultBlock" && b.IsEffectivelyVisible);
            Assert.Contains("Done · 3 files created", block.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            var files = block.GetVisualDescendants().OfType<Button>().Where(b => b.Name != "OpenResultFolderButton").ToList();
            Assert.Equal(EchoDriver.Written[..3].Select(name => $"{name} Rapportages › 2026"), files.Select(_Spoken));
            var openFolder = block.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OpenResultFolderButton");
            openFolder.Command!.Execute(openFolder.CommandParameter);
            files[1].Command!.Execute(files[1].CommandParameter);
        });

        var made = Path.Combine(folder, "Rapportages", "2026");
        Assert.Equal([Path.Combine(folder, "Facturen", "2026-Q3"), made, Path.Combine(made, EchoDriver.Written[1])], opened);
    }

    // AC-1533 criteria 1, 3, 4 and 5: ⋯ says where it works (the worktree, not the project's folder), what it may do,
    // its cost and model; Open folder opens the worktree, Rename lands in header and rail, and Start over asks Panels'
    // confirmation first, then draws the divider and starts the cost at $0 in the same pane.
    [Fact]
    public async Task InTheSimpleStand_DetailsTellWhatAConversationIs_AndOpenRenameAndStartItOver()
    {
        await using var cockpit = JourneyHost.Desktop();
        var repository = await _RepositoryAsync(Path.Combine(cockpit.StateRoot, "repository"));
        var project = await cockpit.SaveProfileAndProjectAsync(repository, isolateInWorktree: true);
        await cockpit.StartDesktopAsync();
        var session = await cockpit.StartSessionThroughTheDialogAsync(project);
        var worktree = cockpit.Driver.WorkingDirectory!;
        var opened = new List<string>();

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = cockpit.Window;
            session.OpenPath = path =>
            {
                opened.Add(path);
                return true;
            };
            cockpit.Cockpit.ShowSimpleViewCommand.Execute(null);
            cockpit.Cockpit.SelectSimpleSessionCommand.Execute(session);
            session.InputText = "hello";
            await session.SendCommand.ExecuteAsync(null);
            await Until.Holds(session, () => session.SessionStatus == SessionStatus.Done);
            window.UpdateLayout();
            var header = window.GetVisualDescendants().OfType<SimpleConversationHeader>().Single(h => h.IsEffectivelyVisible);

            Assert.Equal(
                ["Works in", project.Name, worktree, "Allowed to", "Asks before changing files or running commands.", "Cost", "$0.38 so far", "Model", session.SelectedModel.Label],
                _Details(header).Skip(1).Take(9));

            _Press(header, "DetailsOpenFolder");
            Assert.Equal([worktree], opened);

            _Press(header, "DetailsRename");
            window.UpdateLayout();
            var box = header.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "SimpleHeaderRename" && b.IsEffectivelyVisible);
            box.Text = "Quarterly report";
            box.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            window.UpdateLayout();
            Assert.Contains("Quarterly report", header.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
            Assert.Contains(window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.DataContext == session && b.FindAncestorOfType<ItemsControl>()?.Name?.StartsWith("SimpleRail") == true),
                row => row.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Quarterly report"));

            var panes = cockpit.Cockpit.Sessions.ToList();
            var asked = new TaskCompletionSource<ConfirmationDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (Window.WindowOpenedEvent.AddClassHandler<ConfirmationDialog>((dialog, _) => asked.TrySetResult(dialog)))
            {
                _Press(header, "DetailsStartOver");
                var confirmation = await asked.Task.WaitAsync(Until.Ceiling);
                Assert.DoesNotContain(session.Transcript, row => row.IsDivider);
                confirmation.Close(true);
            }

            await Until.Holds(session, () => session.Transcript.Any(row => row.IsDivider) && session.IsSessionReady);
            window.UpdateLayout();
            Assert.Equal("The earlier conversation was cleared — this conversation starts here", session.Transcript.Last(row => row.IsDivider).Text);
            Assert.Contains("$0.00 so far", _Details(header));
            Assert.Equal(panes, cockpit.Cockpit.Sessions);
            Assert.Same(session, cockpit.Cockpit.SimpleSelectedSession);
        });
    }

    // AC-1533 follow-ups of AC-1529/AC-1530: in a narrow column the header trims its stand and keeps Stop and ⋯ whole
    // beside it, and the composer band names the running step in the same plain words as the stand.
    [Fact]
    public async Task InTheSimpleStand_ANarrowColumnKeepsStopAndDetails_TheComposerStaysSimple_AndBackgroundWorkShowsInTheHeader()
    {
        await using var cockpit = JourneyHost.Desktop();
        var project = await cockpit.SaveProfileAndProjectAsync(Directory.CreateDirectory(Path.Combine(cockpit.StateRoot, "project")).FullName);
        await cockpit.StartDesktopAsync();
        var session = await cockpit.StartSessionThroughTheDialogAsync(project);

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = cockpit.Window;
            window.Width = 900;
            window.Height = 700;
            cockpit.Cockpit.ShowSimpleViewCommand.Execute(null);
            cockpit.Cockpit.SelectSimpleSessionCommand.Execute(session);
            session.InputText = EchoDriver.WorkingPrompt;
            await session.SendCommand.ExecuteAsync(null);
            await Until.Holds(session, () => session.SimpleStatusLabel == "Busy · Creating Kwartaaloverzicht-Q3.xlsx…");
            window.UpdateLayout();

            var header = window.GetVisualDescendants().OfType<SimpleConversationHeader>().Single(h => h.IsEffectivelyVisible);
            var stand = header.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SimpleHeaderStand");
            var stop = header.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SimpleStopButton");
            var details = header.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DetailsButton");
            Assert.True(stop.IsEffectivelyVisible && details.IsEffectivelyVisible);
            Assert.True(stand.TranslatePoint(new Point(stand.Bounds.Width, 0), header)!.Value.X <= stop.TranslatePoint(default, header)!.Value.X);
            Assert.True(details.TranslatePoint(new Point(details.Bounds.Width, 0), header)!.Value.X <= header.Bounds.Width);
            Assert.Equal("Creating Kwartaaloverzicht-Q3.xlsx…", session.ActiveToolActivityLabel);

            // AC-1526 (AC-1529 decision 5): the composer is chip, paperclip, mic and Send under "Reply, or hold <key> to
            // talk…"; Stop, Screenshot and Background stand down, and what runs in the background shows beside Stop.
            await Until.Holds(session, () => session.HasBackgroundTasks);
            window.UpdateLayout();
            SessionView Pane() => window.GetVisualDescendants().OfType<SessionView>().Single(v => v.IsEffectivelyVisible && v.DataContext == session);
            Button Composer(string name) => Pane().GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
            string[] composerOnly = ["BackgroundWorkButton", "ScreenshotButton", "ComposerStopButton"];
            var view = Pane();
            Assert.All(composerOnly, name => Assert.False(Composer(name).IsEffectivelyVisible, name));
            var input = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "InputBox");
            Assert.Equal($"Reply, or hold {cockpit.Cockpit.VoicePushToTalkKeyName} to talk…", input.PlaceholderText);
            var inputRect = new Rect(input.TranslatePoint(default, view) ?? throw new InvalidOperationException("the input is not in the pane"), input.Bounds.Size);
            Assert.All(view.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.FindAncestorOfType<TextBox>() is null),
                button => Assert.False(inputRect.Intersects(new Rect(button.TranslatePoint(default, view) ?? default, button.Bounds.Size)), button.Name));
            var background = header.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SimpleBackgroundButton");
            Assert.True(background.IsEffectivelyVisible);
            Assert.Contains("1", background.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.IsType<BackgroundWorkList>(((Flyout)background.Flyout!).Content);

            cockpit.Cockpit.ShowPanelsViewCommand.Execute(null);
            window.UpdateLayout();
            Assert.All(composerOnly, name => Assert.True(Composer(name).IsEffectivelyVisible, name));
            Assert.NotEqual(Pane().GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "InputBox").PlaceholderText, $"Reply, or hold {cockpit.Cockpit.VoicePushToTalkKeyName} to talk…");
            cockpit.Cockpit.ShowSimpleViewCommand.Execute(null);

            cockpit.Driver.FinishWriting();
            await Until.Holds(session, () => session.SessionStatus == SessionStatus.Done);
        });
    }

    // Opens ⋯ in `header` and reads its popup top to bottom: the heading, then each label and value as they stand.
    private static List<string?> _Details(SimpleConversationHeader header)
    {
        var button = header.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DetailsButton");
        button.Flyout!.ShowAt(button);
        var panel = (StackPanel)((Flyout)button.Flyout).Content!;
        panel.UpdateLayout();
        var words = panel.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.FindAncestorOfType<Button>() is null).Select(t => t.Text).ToList();
        button.Flyout.Hide();
        return words;
    }

    // The machine's load as the heavy-run queue reads it, held where the journey puts it.
    private sealed class FakeMachineLoad : IMachineResources
    {
        public MachineLoad Current { get; private set; } = new(85, 8L << 30, 16L << 30);

        public event Action? Sampled;

        public long? FreeDiskBytes(string directory) => null;

        public void Set(double cpu)
        {
            Current = Current with { CpuPercent = cpu };
            Sampled?.Invoke();
        }
    }

    private static void _Press(SimpleConversationHeader header, string name)
    {
        var button = header.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DetailsButton");
        button.Flyout!.ShowAt(button);
        var action = ((Control)((Flyout)button.Flyout).Content!).GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
        Assert.True(action.IsEffectivelyEnabled);
        action.Command!.Execute(action.CommandParameter);
        button.Flyout.Hide();
    }

    // What a step line or a file button puts on screen: a step's ✓ or clock, then its visible words.
    private static string _Spoken(Control line) => string.Join(' ', line.GetVisualDescendants()
        .Select(part => part switch
        {
            Material.Icons.Avalonia.MaterialIcon { Kind: Material.Icons.MaterialIconKind.Check } => "✓",
            Material.Icons.Avalonia.MaterialIcon { Kind: Material.Icons.MaterialIconKind.ClockOutline } => "◷",
            TextBlock text when text.IsEffectivelyVisible => text.Text,
            _ => null,
        })
        .Where(word => !string.IsNullOrEmpty(word)));

    private static async Task<string> _RepositoryAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        await _GitAsync(folder, "init", "-b", "main");
        await _GitAsync(folder, "-c", "user.name=Journey", "-c", "user.email=journey@localhost", "commit", "--allow-empty", "-m", "initial");
        return folder;
    }

    private static async Task<string> _BranchesAsync(string repository, string branch) =>
        (await _GitAsync(repository, "branch", "--list", branch)).Trim();

    private static async Task<string> _GitAsync(string folder, params string[] arguments)
    {
        var result = await GitCli.RunAsync(folder, arguments, CancellationToken.None);
        return result.ExitCode == 0 ? result.StandardOutput : throw new InvalidOperationException($"git {string.Join(' ', arguments)}: {result.StandardError}");
    }
}
