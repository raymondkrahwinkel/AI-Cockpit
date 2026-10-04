using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.BackendApi;

namespace Cockpit.Journeys;

// J9 (AC-1456), a server in the session list: the desktop connects to Cockpit.Server with a key, as the connect form leaves
// it, through a relay that can drop the line. S17b (AC-1469) works in the pane; S18 (AC-1457) ends with the Health tab.
[Collection(JourneyCollection.Alone)]
public sealed class RemoteSessionJourney
{
    private const string Server = "journey-server";

    // The group shows the server's sessions; a start answers in its pane; a lost line keeps it, and the composer and a
    // permission answer reach the server; stop asks once. Then Health: an expired login is badge and alarm until Sign
    // in again clears both, and Run now starts the scheduled flow there. AC-1479: the holder of the assistant opens its
    // window on the server and is answered there; a key that does not hold it is refused (403) and starts nothing.
    [Fact]
    public async Task AServerGroup_StartsASessionThere_KeepsItsPaneThroughAReconnect_AndStopsItAfterOneConfirmation()
    {
        var root = Directory.CreateTempSubdirectory("journey-remote-").FullName;
        var stateRoot = Path.Combine(root, "state");
        var key = "ck_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var (fingerprint, holderKey, plainKey) = await ServerJourney._PrepareStateRootAsync(stateRoot, 0, root, "http://127.0.0.1:9/webhook", withTerminalProfile: false, withAssistant: true);
        var run = ServerJourney._RunServer(
            ServerJourney._Metadata("CockpitServerDirectory"), stateRoot, Path.Combine(root, "unlock"), ServerJourney._Secret(root, "connect-key", key));
        try
        {
            await run.Running.WaitAsync(Until.Ceiling);
            var port = ServerJourney._McpPort(run.Output);
            await using var relay = new Relay(port);
            using var admin = new BackendApiClient(new Uri($"https://127.0.0.1:{port}/"), key, fingerprint, TimeProvider.System);
            using var holder = new BackendApiClient(new Uri($"https://127.0.0.1:{port}/"), holderKey, fingerprint, TimeProvider.System);
            using var plain = new BackendApiClient(new Uri($"https://127.0.0.1:{port}/"), plainKey, fingerprint, TimeProvider.System);
            var refusedPrompt = await Assert.ThrowsAsync<BackendApiException>(() => plain.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/assistant/prompt", new { text = "let me in" }));

            await using var cockpit = JourneyHost.Desktop();
            var project = await cockpit.SaveProfileAndProjectAsync(Directory.CreateDirectory(Path.Combine(cockpit.StateRoot, "project")).FullName);
            await cockpit.Services.GetRequiredService<IMcpServerStore>().SaveAsync(
            [
                new McpServerConfig
                {
                    Id = McpServerIdentity.NewId(),
                    Name = NodeServerName.For(Server, NodeServerName.SessionsServerName),
                    Transport = McpTransport.Http,
                    Scope = McpServerScope.LocalOnly,
                    Url = $"https://127.0.0.1:{relay.Port}/mcp",
                    Auth = McpServerAuth.ApiKey,
                    ApiKey = holderKey,
                    PinnedCertificateFingerprint = fingerprint,
                },
            ]);
            await cockpit.StartDesktopAsync();
            var local = await cockpit.StartSessionThroughTheDialogAsync(project);

            var localClosedAtOnce = false;
            var pane = default(SessionViewModel);
            var paneId = "";
            var reconnecting = "";
            var paneStayed = false;
            var askedOnce = false;
            var keptRunning = false;
            var composerOffWhileDown = false;
            var unsentKept = false;
            var mayAnswer = false;
            var answerRows = 0;
            var badgeWhenExpired = "";
            var alarmWhenExpired = false;
            var signInAwaitedInput = false;
            var clearedAfterSignIn = false;
            var codeValidFor = "";
            var scheduleShown = "";
            var zoneHeading = "";
            var runBefore = "";
            var runAfter = "";
            var canOpenBefore = true;
            var assistantWindow = default(AssistantChatWindow);
            var assistantChat = default(AssistantChatViewModel);
            var view = cockpit.Cockpit;
            var assistantClosed = Task.CompletedTask;
            var opened = new TaskCompletionSource<AssistantChatWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watch = Window.WindowOpenedEvent.AddClassHandler<AssistantChatWindow>((window, _) => opened.TrySetResult(window));
            await HeadlessAvalonia.RunAsync(async () =>
            {
                // A local pane that is not busy closes on its click, as before.
                await view.RequestCloseSessionCommand.ExecuteAsync(local);
                localClosedAtOnce = !view.Sessions.Contains(local) && !local.IsConfirmingClose;

                await Until.CollectionHolds(view.ServerGroups, () => view.ServerGroups.Count == 1);
                var group = view.ServerGroups[0];
                await Until.Holds(group, () => group.IsConnected);

                // The holder's key offers the assistant; the refused key started nothing. Its window is the assistant's
                // own, and what is typed there is answered by the server.
                await Until.Holds(group, () => group.HoldsAssistant);
                canOpenBefore = group.CanOpenAssistant;
                await holder.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/assistant/prompt", new { text = "wake" });
                await Until.Holds(group, () => group.CanOpenAssistant);
                assistantClosed = view.OpenServerAssistantCommand.ExecuteAsync(group);
            });
            assistantWindow = await opened.Task.WaitAsync(Until.Ceiling);
            await HeadlessAvalonia.RunAsync(async () =>
            {
                assistantChat = assistantWindow.DataContext as AssistantChatViewModel ?? throw new InvalidOperationException("The assistant window has no conversation.");
                assistantChat.InputText = "hello from the laptop";
                await assistantChat.SendCommand.ExecuteAsync(null);
                var transcript = assistantChat.Session?.Transcript ?? throw new InvalidOperationException("The window follows no assistant.");
                await Until.ItemsHold(transcript, () => transcript.Any(row => row.Kind == TranscriptEntryKind.AssistantText && row.Text.Contains("echo: hello from the laptop", StringComparison.Ordinal)));
                assistantWindow.Close();
            });
            await assistantClosed.WaitAsync(Until.Ceiling);
            await HeadlessAvalonia.RunAsync(async () =>
            {
                var group = view.ServerGroups[0];

                await view.OpenServerStartCommand.ExecuteAsync(group);
                group.Start.SelectedProfile = group.Start.Profiles.First(profile => profile.Label == "Echo");
                group.Start.Prompt = "hello";
                await view.StartOnServerCommand.ExecuteAsync(group);
                var row = group.Sessions.Single();
                paneId = row.Handle.PaneId;
                pane = row.Pane as SessionViewModel ?? throw new InvalidOperationException("The start opened no pane.");
                await Until.ItemsHold(pane.Transcript, () => _Count(pane, "echo: hello") == 1);

                // The line drops: Reconnecting, the pane stays, and the server answers on without anyone watching.
                relay.Cut();
                await Until.Holds(group, () => !group.IsConnected);
                reconnecting = group.StatusLabel;

                // The composer and the buttons are off while the line is down, and what was typed stays unsent.
                pane.InputText = "lost";
                await pane.SendCommand.ExecuteAsync(null);
                composerOffWhileDown = !pane.IsInputEnabled && !pane.CanSend && !pane.IsLinkUp;
                unsentKept = pane.InputText == "lost";
                pane.InputText = "";
                paneStayed = ReferenceEquals(group.Sessions.Single().Pane, pane) && view.GridPanes.Contains(pane);
                using var answered = new CancellationTokenSource(Until.Ceiling);
                await admin.SendAsync<JsonObject>(HttpMethod.Post, $"api/v1/sessions/{paneId}/prompt", new { text = "again" });
                await admin.StreamEventsAsync(0, answered.Token)
                    .FirstAsync(evt => evt.Kind == "row" && evt.Data.GetRawText().Contains("echo: again", StringComparison.Ordinal), answered.Token);
                relay.Restore();
                await Until.Holds(group, () => group.IsConnected);
                try
                {
                    await Until.ItemsHold(pane.Transcript, () => _Count(pane, "echo: again") > 0);
                }
                catch (TimeoutException)
                {
                    // What the pane drew and what both ends logged, so a red run names its own cause.
                    var rows = string.Join(Environment.NewLine, pane.Transcript.Select(entry => $"  {entry.Kind}: {entry.Text}"));
                    throw new TimeoutException(string.Join(
                        Environment.NewLine,
                        $"The pane never drew \"echo: again\" after the reconnect ({group.StatusLabel}).",
                        "Rows:",
                        rows,
                        "Desktop log:",
                        cockpit.LogText,
                        "Server:",
                        run.Output));
                }

                // The composer comes back by itself; its message goes to the server, and so does the answer to a permission.
                await Until.Holds(pane, () => pane.IsInputEnabled && pane.IsLinkUp);
                mayAnswer = pane.MayAnswerPermissions;
                pane.InputText = "second";
                await pane.SendCommand.ExecuteAsync(null);
                await Until.ItemsHold(pane.Transcript, () => _Count(pane, "echo: second") == 1);
                pane.InputText = "ask";
                await pane.SendCommand.ExecuteAsync(null);
                await Until.ItemsHold(pane.Transcript, () => pane.Transcript.Any(row => row.IsPendingPermission));
                await pane.AllowToolCommand.ExecuteAsync(pane.Transcript.First(row => row.IsPendingPermission));
                await Until.ItemsHold(pane.Transcript, () => _Count(pane, "echo: allowed echo-ask") == 1);
                answerRows = _Count(pane, "echo: allowed echo-ask");

                // Stop asks once; Keep running leaves it running there, Stop on server ends it there.
                await view.RequestCloseSessionCommand.ExecuteAsync(pane);
                askedOnce = pane.IsConfirmingClose && group.Sessions.Count == 1;
                view.CancelCloseSessionCommand.Execute(pane);
                keptRunning = !pane.IsConfirmingClose && await _ListsAsync(admin, paneId);
                await view.RequestCloseSessionCommand.ExecuteAsync(pane);
                await view.ConfirmCloseSessionCommand.ExecuteAsync(pane);
                await Until.CollectionHolds(group.Sessions, () => group.Sessions.Count == 0);

                // The profile's sign-in is taken away on the server; once the server reads it expired, the desktop reads
                // the health again (what its own poll does) and the header shows one open alarm before the tab is open.
                File.Delete(Path.Combine(root, ServerJourney.SignedInFile));
                await _WaitUntilServerReadsExpiredAsync(admin);

                await group.Server.Health.RefreshAsync();
                await Until.Holds(group.Health, () => group.Health.HasBadge);
                badgeWhenExpired = group.Health.BadgeText;
                view.ToggleServerHealthCommand.Execute(group);
                await Until.Holds(group.Health, () => group.Health.ShowsAlarm);
                alarmWhenExpired = group.Health.AlarmTitle.Contains("journey-server", StringComparison.Ordinal) && group.Health.CanSignInAgain;

                // Sign in again plays the provider's own step; the code goes to the server, and alarm and badge go.
                await group.Health.StartSignInCommand.ExecuteAsync(null);
                var flow = group.Health.SignIn ?? throw new InvalidOperationException("Sign in again opened no sign-in.");
                try
                {
                    await Until.Holds(flow, () => flow.AwaitsInput);
                }
                catch (TimeoutException)
                {
                    // What the flow showed and what both ends logged, so a red run names its own cause.
                    throw new TimeoutException($"The sign-in never asked for input. Message: {flow.Message}; error: {flow.ErrorMessage}; completed: {flow.IsCompleted}.{Environment.NewLine}Desktop log:{Environment.NewLine}{cockpit.LogText}{Environment.NewLine}Server:{Environment.NewLine}{run.Output}");
                }

                signInAwaitedInput = flow.Message.Contains("paste the code", StringComparison.Ordinal);
                codeValidFor = flow.CodeValidFor;
                flow.CodeInput = "echo-code";
                await flow.SubmitCommand.ExecuteAsync(null);
                await Until.Holds(group.Health, () => !group.Health.ShowsAlarm && !group.Health.HasBadge);
                clearedAfterSignIn = group.Health.Profiles.Any(profile => profile.IsSignedIn && profile.Label == "EchoSignIn");

                // Run now on the scheduled flow: the row says what the server's run came to.
                var scheduled = group.Health.Runs.Single(run => run.Workflow == "Journey scheduled");
                runBefore = scheduled.Outcome;
                scheduleShown = scheduled.Schedule;
                zoneHeading = group.Health.RunsDetail;
                await group.Health.RunNowCommand.ExecuteAsync(scheduled);
                await Until.Holds(group.Health, () => group.Health.Runs.Single(run => run.Workflow == "Journey scheduled").Outcome.StartsWith("Done", StringComparison.Ordinal));
                runAfter = group.Health.Runs.Single(run => run.Workflow == "Journey scheduled").Outcome;
            });

            Assert.Equal(HttpStatusCode.Forbidden, refusedPrompt.Status);
            Assert.False(canOpenBefore, "A key without holdsAssistant started the server's assistant.");
            var answeredThere = await holder.GetAsync<JsonObject>("api/v1/assistant/transcript");
            Assert.Contains("echo: hello from the laptop", answeredThere["entries"]?.ToJsonString() ?? "", StringComparison.Ordinal);
            Assert.True(localClosedAtOnce, "A local pane that is not busy asked before it closed.");
            Assert.Equal("Reconnecting", reconnecting);
            Assert.True(paneStayed, "The remote pane went away while the line was down.");
            Assert.NotNull(pane);
            Assert.Equal((1, 1), (_Count(pane, "echo: hello"), _Count(pane, "echo: again")));
            Assert.True(composerOffWhileDown, "The composer stayed on while the line was down.");
            Assert.True(unsentKept, "A message typed while the line was down was sent or lost.");
            Assert.Equal(0, _Count(pane, "echo: lost"));
            Assert.True(mayAnswer, "The key's grant to answer permissions did not reach the pane.");
            Assert.Equal((1, 1), (_Count(pane, "echo: second"), answerRows));
            Assert.True(askedOnce, "Stop on a remote pane did not ask first.");
            Assert.True(keptRunning, "Keep running did not leave the session running on the server.");
            Assert.False(await _ListsAsync(admin, paneId), "Stop on server left the session running there.");
            Assert.DoesNotContain(pane, HeadlessAvalonia.Run(() => view.GridPanes.ToList()));
            Assert.Equal("1", badgeWhenExpired);
            Assert.True(alarmWhenExpired, "The tab did not show the expired login as the alarm with Sign in again.");
            Assert.True(signInAwaitedInput, "Sign in again did not show the provider's step.");
            Assert.True(clearedAfterSignIn, "The alarm or the badge outlived the sign-in.");
            Assert.Matches(@"^code valid for (0\d|1[0-4]):[0-5]\d$", codeValidFor);
            Assert.Equal("once 2099-01-01 00:00", scheduleShown);
            Assert.Contains("UTC", zoneHeading, StringComparison.Ordinal);
            Assert.Equal("Not run · Never", runBefore);
            Assert.StartsWith("Done", runAfter, StringComparison.Ordinal);
            Assert.StartsWith("Journey scheduled · Done", await _ServerRunLabelAsync(admin), StringComparison.Ordinal);
        }
        finally
        {
            if (!run.Process.HasExited)
            {
                run.Process.Kill(entireProcessTree: true);
            }

            run.Process.Dispose();
            JourneyHost.RemoveStateRoot(root);
        }
    }

    private static async Task _WaitUntilServerReadsExpiredAsync(BackendApiClient admin)
    {
        using var ceiling = new CancellationTokenSource(Until.Ceiling);
        while (true)
        {
            var health = await admin.GetAsync<JsonObject>("api/v1/health", ceiling.Token);
            if (health["profiles"]?.AsArray().Any(profile => profile?["signIn"]?.GetValue<string>() == "expired") == true)
            {
                return;
            }

            await Task.Delay(100, ceiling.Token);
        }
    }

    // What the server itself says the scheduled flow's last run came to, read through its own health route.
    private static async Task<string> _ServerRunLabelAsync(BackendApiClient admin)
    {
        var health = await admin.GetAsync<JsonObject>("api/v1/health");
        return health["sections"]?.AsArray().FirstOrDefault(section => section?["name"]?.GetValue<string>() == "workflows-runs")?["rows"]?.AsArray()
            .Select(row => row?["label"]?.GetValue<string>() ?? "")
            .FirstOrDefault(label => label.StartsWith("Journey scheduled · ", StringComparison.Ordinal) && !label.EndsWith("Next run", StringComparison.Ordinal)) ?? "";
    }

    private static int _Count(SessionViewModel pane, string text) =>
        pane.Transcript.Count(row => row.Kind == TranscriptEntryKind.AssistantText && row.Text.Contains(text, StringComparison.Ordinal));

    private static async Task<bool> _ListsAsync(BackendApiClient admin, string paneId) =>
        (await admin.GetAsync<JsonObject>("api/v1/sessions"))["sessions"]?.AsArray().Any(session => session?["paneId"]?.GetValue<string>() == paneId) == true;

    // A TCP relay between the desktop and the server that can be cut: it drops every connection and turns new ones away
    // until restored, which is how a laptop leaving the network looks to both ends. TLS passes through untouched.
    private sealed class Relay : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly int _target;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<TcpClient> _open = [];
        private readonly Task _accepting;
        private volatile bool _cut;

        public Relay(int target)
        {
            _target = target;
            _listener.Start();
            _accepting = _AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Cut()
        {
            lock (_open)
            {
                _cut = true;
                foreach (var connection in _open)
                {
                    connection.Dispose();
                }

                _open.Clear();
            }
        }

        public void Restore() => _cut = false;

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            Cut();
            await _accepting;
            _stop.Dispose();
        }

        private async Task _AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                if (_cut)
                {
                    client.Dispose();
                    continue;
                }

                _ = _PipeAsync(client);
            }
        }

        // Every failure here is the cut or the end of a connection, which is all a relay has to say about it.
        private async Task _PipeAsync(TcpClient client)
        {
            // Checked again under the lock Cut holds: a connection accepted just before a cut must not outlive it.
            var server = new TcpClient();
            lock (_open)
            {
                if (_cut)
                {
                    client.Dispose();
                    server.Dispose();
                    return;
                }

                _open.Add(client);
                _open.Add(server);
            }

            try
            {
                await server.ConnectAsync(IPAddress.Loopback, _target, _stop.Token);
                var near = client.GetStream();
                var far = server.GetStream();
                await Task.WhenAny(near.CopyToAsync(far, _stop.Token), far.CopyToAsync(near, _stop.Token));
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
            }
            finally
            {
                client.Dispose();
                server.Dispose();
            }
        }
    }
}
