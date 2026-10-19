using System.Net;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Backup;
using Cockpit.Plugins.Abstractions.Mcp;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1509, acceptance 3: route C keeps a Depot connection only once the host's sign-in succeeded. While it signs in, the
// Depot is offered to the host (so the sign-in can find it) and is not in storage; skipped or offline, nothing is left.
public sealed class DepotFirstStartTests
{
    [Theory]
    [InlineData(PluginMcpSignInOutcome.Unreachable, DepotFirstStartState.Offline)]
    [InlineData(PluginMcpSignInOutcome.Declined, DepotFirstStartState.NotSignedIn)]
    [InlineData(PluginMcpSignInOutcome.Authorized, DepotFirstStartState.SignedIn)]
    public async Task A_connection_is_kept_only_after_a_sign_in_that_succeeded(PluginMcpSignInOutcome outcome, string state)
    {
        var host = Substitute.For<ICockpitHost>();
        host.Storage.Returns(new FakePluginStorage());
        host.CreateAuthorizedHandler(Arg.Any<string>()).Returns(_ => new _NotFound());
        Func<JsonElement, CancellationToken, Task<JsonElement>>? handler = null;
        host.Channel.Handle(DepotChannel.FirstStartSignIn, Arg.Do<Func<JsonElement, CancellationToken, Task<JsonElement>>>(handle => handler = handle));
        using var plugin = new DepotPlugin();
        var stored = () => new Settings.DepotSettings(host.Storage).Connections;
        (IReadOnlyList<string> Offered, int Stored)? whileSigningIn = null;
        host.SignInMcpServerAsync("Depot: depot.test", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            whileSigningIn = ([.. plugin.GetMcpServers().Select(server => server.Url)], stored().Count);
            return outcome;
        });
        plugin.InitializeFirstStart(host);

        Assert.NotNull(handler);
        var signIn = handler;
        var answer = (await signIn(JsonSerializer.SerializeToElement(new DepotFirstStartSignInRequest("https://depot.test/mcp"), DepotChannel.Json), CancellationToken.None))
            .Deserialize<DepotFirstStartAnswer>(DepotChannel.Json);

        Assert.NotNull(whileSigningIn);
        Assert.Equal(["https://depot.test/mcp"], whileSigningIn.Value.Offered);
        Assert.Equal(0, whileSigningIn.Value.Stored);
        Assert.Equal(state, answer?.State);
        var signedIn = outcome == PluginMcpSignInOutcome.Authorized;
        string[] kept = signedIn ? ["https://depot.test"] : [];
        Assert.Equal(kept, stored().Select(connection => connection.Url));
        Assert.Equal(signedIn ? 1 : 0, plugin.GetMcpServers().Count);
        host.Received(signedIn ? 1 : 0).AddBackupTarget(Arg.Any<IBackupTarget>());

        // A Depot without /cockpit/manifest answers 404: C2 is not offered, and that is not reported as a problem.
        Assert.Null(answer?.Manifest);
        Assert.DoesNotContain(answer?.Notes ?? [], note => note.Contains("profile", StringComparison.Ordinal));
    }

    private sealed class _NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
