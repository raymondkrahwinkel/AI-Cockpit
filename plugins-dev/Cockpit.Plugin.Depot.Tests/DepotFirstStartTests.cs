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
        host.CreateAuthorizedHandler(Arg.Any<string>(), "depot.host").Returns(_ => new _NotFound());
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_profile_with_multiple_organizations_requires_the_selected_organization(bool multipleOrganizations)
    {
        var host = Substitute.For<ICockpitHost>();
        host.Storage.Returns(new FakePluginStorage());
        var depot = new _ManifestHandler(multipleOrganizations);
        host.CreateAuthorizedHandler(Arg.Any<string>(), "depot.host").Returns(_ => depot);
        host.SignInMcpServerAsync("Depot: depot.test", Arg.Any<CancellationToken>()).Returns(PluginMcpSignInOutcome.Authorized);
        Func<JsonElement, CancellationToken, Task<JsonElement>>? signIn = null;
        Func<JsonElement, CancellationToken, Task<JsonElement>>? manifest = null;
        host.Channel.Handle(DepotChannel.FirstStartSignIn, Arg.Do<Func<JsonElement, CancellationToken, Task<JsonElement>>>(handle => signIn = handle));
        host.Channel.Handle(DepotChannel.FirstStartManifest, Arg.Do<Func<JsonElement, CancellationToken, Task<JsonElement>>>(handle => manifest = handle));
        using var plugin = new DepotPlugin();
        plugin.InitializeFirstStart(host);

        Assert.NotNull(signIn);
        var answer = (await signIn(JsonSerializer.SerializeToElement(new DepotFirstStartSignInRequest("https://depot.test"), DepotChannel.Json), CancellationToken.None))
            .Deserialize<DepotFirstStartAnswer>(DepotChannel.Json);

        if (answer is null)
        {
            throw new InvalidOperationException("The first-start response was empty.");
        }
        // The sign-in lists the backups (C1) before it reads the manifest (C2); only the manifest reads are at stake here.
        var manifestReads = depot.Requests.Where(uri => uri.AbsolutePath == "/cockpit/manifest").ToList();
        Assert.Empty(manifestReads[0].Query);
        if (!multipleOrganizations)
        {
            Assert.Equal("{}", answer.Manifest);
            Assert.Null(answer.Candidates);
            Assert.Single(manifestReads);
            return;
        }

        Assert.Null(answer.Manifest);
        if (answer.Candidates is not { } candidates)
        {
            throw new InvalidOperationException("The organization choices were missing.");
        }

        if (answer.TargetId is not { } targetId)
        {
            throw new InvalidOperationException("The Depot target was missing.");
        }

        if (manifest is null)
        {
            throw new InvalidOperationException("The selected-manifest action was not registered.");
        }

        Assert.Equal(["acme", "globex"], candidates.Select(candidate => candidate.Slug));
        var selected = (await manifest(JsonSerializer.SerializeToElement(new DepotFirstStartManifestRequest(targetId, "globex"), DepotChannel.Json), CancellationToken.None))
            .Deserialize<DepotFirstStartManifestAnswer>(DepotChannel.Json);

        Assert.Equal("{}", selected?.Manifest);
        Assert.Equal("?org=globex", depot.Requests.Last(uri => uri.AbsolutePath == "/cockpit/manifest").Query);
    }

    private sealed class _NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class _ManifestHandler(bool multipleOrganizations) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri ?? throw new InvalidOperationException("The request URI was missing.");
            Requests.Add(requestUri);
            if (requestUri.AbsolutePath == "/cockpit/backups")
            {
                return Task.FromResult(_Response(HttpStatusCode.OK, "[]", "application/json"));
            }

            if (multipleOrganizations && string.IsNullOrEmpty(requestUri.Query))
            {
                return Task.FromResult(_Response(HttpStatusCode.MultipleChoices, """{"format":"cockpit.provision.choice","formatVersion":1,"organizations":[{"slug":"acme","name":"Acme BV"},{"slug":"globex","name":"Globex"}]}""", "application/vnd.cockpit.provision-choice+json"));
            }

            return Task.FromResult(_Response(HttpStatusCode.OK, "{}", "application/vnd.cockpit.provision+json"));
        }

        private static HttpResponseMessage _Response(HttpStatusCode statusCode, string json, string contentType) =>
            new(statusCode) { Content = new StringContent(json, System.Text.Encoding.UTF8, contentType) };
    }
}
