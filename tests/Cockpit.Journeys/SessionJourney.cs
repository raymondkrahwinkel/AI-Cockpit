using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.HeavyRuns;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.StatusBar;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cockpit.Journeys;

// The product's core act on the desktop: a session started from a project and a profile, a prompt through its
// composer and the answer in its pane. The route the operator takes, through the real New session dialog.
[Collection(JourneyCollection.Alone)]
public sealed class SessionJourney
{
    // J2: the dialog opens the pane, the answer lands busy then done (AC-1437); Stop is no failure (AC-1031/1449), a
    // failed turn offers Retry from the event log (AC-728/1438). AC-1535: four heavy runs on a busy machine: one runs,
    // three wait with a reason, a closed session's place comes back, and the pre-merge gate starts first.
    [Fact]
    public async Task ASessionStartedFromAProjectAndAProfile_AnswersInItsPane()
    {
        var load = new FakeMachineLoad();
        var memory = new MemoryNotes();
        await using var cockpit = JourneyHost.Desktop(configure: services =>
        {
            services.AddSingleton<IMachineResources>(load);
            services.AddSingleton<IProjectMemoryNotes>(memory);
        });
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

        // The verdicts reach the session's inbox, attached to its next tool result in the order the runs ended.
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

        // AC-1658: a quick note from the palette lands in the project's memory, and its window closes on the save.
        await HeadlessAvalonia.RunAsync(async () =>
        {
            cockpit.Cockpit.BuildPaletteCommands().Single(command => command.Title == "Quick note").Invoke();
            var window = cockpit.Cockpit.QuickNotes?.Window ?? throw new InvalidOperationException("The quick note did not open.");
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "NoteBox").Text = "remember the queue";
            var save = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save"));
            save.Command?.Execute(save.CommandParameter);
            await closed.Task.WaitAsync(Until.Ceiling);
        });
        Assert.Equal([(project.Id, "remember the queue")], memory.Notes);
    }

    // The project's memory as the quick note writes to it: every note lands, and is kept for the journey to read.
    private sealed class MemoryNotes : IProjectMemoryNotes
    {
        public List<(string ProjectId, string Note)> Notes { get; } = [];

        public bool CanAppend(Project project) => true;

        public Task<ProjectMemoryAppendResult> AppendAsync(Project project, string note, CancellationToken cancellationToken)
        {
            Notes.Add((project.Id, note));
            return Task.FromResult(new ProjectMemoryAppendResult(ProjectMemoryAppendOutcome.Success, null));
        }
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
}
