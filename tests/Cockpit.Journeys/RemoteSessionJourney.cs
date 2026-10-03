using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.BackendApi;

namespace Cockpit.Journeys;

// J9 (AC-1456), a server in the session list: the desktop connects to Cockpit.Server with a key, as the connect form leaves
// it, through a relay that can drop the line. S17b (AC-1469) works in the pane; S18 adds its steps here.
[Collection(JourneyCollection.Alone)]
public sealed class RemoteSessionJourney
{
    private const string Server = "journey-server";

    // The group shows the server's sessions; a start with a first message answers in its pane; a lost line shows
    // Reconnecting and keeps the pane, which after the return holds every row once; a second message and a permission
    // answered there reach the server; stop asks once, and only remote.
    [Fact]
    public async Task AServerGroup_StartsASessionThere_KeepsItsPaneThroughAReconnect_AndStopsItAfterOneConfirmation()
    {
        var root = Directory.CreateTempSubdirectory("journey-remote-").FullName;
        var stateRoot = Path.Combine(root, "state");
        var key = "ck_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var (fingerprint, _) = await ServerJourney._PrepareStateRootAsync(stateRoot, 0, root, "http://127.0.0.1:9/webhook");
        var run = ServerJourney._RunServer(
            ServerJourney._Metadata("CockpitServerDirectory"), stateRoot, Path.Combine(root, "unlock"), ServerJourney._Secret(root, "connect-key", key));
        try
        {
            await run.Running.WaitAsync(Until.Ceiling);
            var port = ServerJourney._McpPort(run.Output);
            await using var relay = new Relay(port);
            using var admin = new BackendApiClient(new Uri($"https://127.0.0.1:{port}/"), key, fingerprint, TimeProvider.System);

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
                    ApiKey = key,
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
            var view = cockpit.Cockpit;
            await HeadlessAvalonia.RunAsync(async () =>
            {
                // A local pane that is not busy closes on its click, as before.
                await view.RequestCloseSessionCommand.ExecuteAsync(local);
                localClosedAtOnce = !view.Sessions.Contains(local) && !local.IsConfirmingClose;

                await Until.CollectionHolds(view.ServerGroups, () => view.ServerGroups.Count == 1);
                var group = view.ServerGroups[0];
                await Until.Holds(group, () => group.IsConnected);

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
            });

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
