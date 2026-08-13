using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSharedProjectSource.ListPublishTargetsAsync`/`PublishAsync` (AC-620): turning a not-yet-shared local
// project into a new `.cockpit/project.json`. Fixtures are real JSON shapes Depot's tools send, including the
// `[NotFound]` read-error text, measured live against a real Depot server.
public class DepotSharedProjectSourcePublishTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Work", "https://depot.example.com");

    private static ISharedProjectSource SourceFor(ICockpitHost host, HttpClient? httpClient = null) =>
        DepotMemorySource.BuildSharedProjectSources([Connection()], host, httpClient).Single();

    private sealed class _StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static string _Scheme(ICockpitHost host) =>
        DepotMemorySource.BuildRegistrationPairs([Connection()], host).Single().Registration.Scheme;

    private static void _StubListProjects(ICockpitHost host, string json) =>
        host.CallMcpToolAsync(Arg.Any<string>(), "list_projects", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success(json)));

    private static void _StubRead(ICockpitHost host, string slug, PluginMcpToolCallResult result) =>
        host.CallMcpToolAsync(
            Arg.Any<string>(), "read",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => args != null && (string)args["project"]! == slug),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));

    private static PluginMcpToolCallResult _NotFound(string slug) =>
        PluginMcpToolCallResult.Failed($"[NotFound] '.cockpit/project.json' was not found in project '{slug}'.");

    private static PluginMcpToolCallResult _DefinitionEnvelope(string definitionJson, string checksum = "chk") =>
        PluginMcpToolCallResult.Success(
            $$"""{"path":".cockpit/project.json","content":{{System.Text.Json.JsonSerializer.Serialize(definitionJson)}},"checksum":"{{checksum}}","size":1}""");

    // Captures the raw `content` JSON PublishAsync sends, so a test can assert on the actual wire text rather than
    // trusting a claim about it — the DoD's own "measured, not guessed" bar for the secret-exclusion test below.
    private static Func<string> _StubWriteCapturingRawContent(ICockpitHost host, string slug, PluginMcpToolCallResult result)
    {
        string? sent = null;
        host.CallMcpToolAsync(
            Arg.Any<string>(), "write",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => args != null && (string)args["project"]! == slug),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                sent = (string)callInfo.ArgAt<IReadOnlyDictionary<string, object?>?>(2)!["content"]!;
                return Task.FromResult(result);
            });
        return () => sent ?? throw new InvalidOperationException("write was never called");
    }

    private static SharedProjectPublishDefinition Definition(
        string name = "New Project",
        IReadOnlyList<SharedProjectPublishResource>? resources = null) =>
        new(name, Description: "A fresh project", GitUrl: "git@github.com:example/new-project.git",
            BehaviorPrompt: "Be terse.", IsolateInWorktreeByDefault: false, EnabledMcpServerNames: ["github"],
            Resources: resources ?? []);

    [Fact]
    public async Task PublishAsync_NoDefinitionYet_WritesAndReturnsTheBoundId()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "new-project", _NotFound("new-project"));
        var written = _StubWriteCapturingRawContent(host, "new-project", PluginMcpToolCallResult.Success("""{"checksum":"chk1"}"""));

        var result = await SourceFor(host).PublishAsync($"{scheme}:new-project", Definition(), CancellationToken.None);

        Assert.Equal(SharedProjectPublishOutcome.Success, result.Outcome);
        Assert.Equal($"{scheme}:new-project", result.BoundId);
        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(written(), out var sent, out _));
        Assert.Equal("New Project", sent!.Name);
        Assert.Equal("git@github.com:example/new-project.git", sent.GitUrl);
    }

    [Fact]
    public async Task PublishAsync_WritesWithNoBaseChecksum_ThisIsTheFirstWriteForThisProject()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "new-project", _NotFound("new-project"));
        _StubWriteCapturingRawContent(host, "new-project", PluginMcpToolCallResult.Success("""{"checksum":"chk1"}"""));

        await SourceFor(host).PublishAsync($"{scheme}:new-project", Definition(), CancellationToken.None);

        await host.Received(1).CallMcpToolAsync(
            Arg.Any<string>(), "write",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => args != null && !args.ContainsKey("baseChecksum")),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_ProjectHasALogo_UploadsItThenWritesTheBlobPath()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "new-project", _NotFound("new-project"));
        host.CallMcpToolAsync(Arg.Any<string>(), "request_upload", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"uploadUrl":"https://depot.example.com/blob/upload/abc"}""")));
        var written = _StubWriteCapturingRawContent(host, "new-project", PluginMcpToolCallResult.Success("""{"checksum":"chk1"}"""));
        using var httpClient = new HttpClient(new _StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Created)));

        var result = await SourceFor(host, httpClient).PublishAsync(
            $"{scheme}:new-project", Definition() with { LogoBytes = [1, 2, 3] }, CancellationToken.None);

        Assert.Equal(SharedProjectPublishOutcome.Success, result.Outcome);
        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(written(), out var sent, out _));
        Assert.Equal(CockpitProjectLogoBlob.BlobPath, sent!.Logo);
    }

    [Fact]
    public async Task PublishAsync_LogoUploadFails_ReportsFailedWithoutWritingTheDefinitionAtAll()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "new-project", _NotFound("new-project"));
        host.CallMcpToolAsync(Arg.Any<string>(), "request_upload", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("no permission")));

        var result = await SourceFor(host).PublishAsync(
            $"{scheme}:new-project", Definition() with { LogoBytes = [1, 2, 3] }, CancellationToken.None);

        Assert.Equal(SharedProjectPublishOutcome.Failed, result.Outcome);
        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_NoLogo_NeverAttemptsAnUpload()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "new-project", _NotFound("new-project"));
        _StubWriteCapturingRawContent(host, "new-project", PluginMcpToolCallResult.Success("""{"checksum":"chk1"}"""));

        await SourceFor(host).PublishAsync($"{scheme}:new-project", Definition(), CancellationToken.None);

        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), "request_upload", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_WriteRejectedForRoleBelowEditor_ReturnsPermissionDenied()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "new-project", _NotFound("new-project"));
        host.CallMcpToolAsync(
            Arg.Any<string>(), "write",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => args != null && (string)args["project"]! == "new-project"),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("This action requires the Editor role on project 'new-project'.")));

        var result = await SourceFor(host).PublishAsync($"{scheme}:new-project", Definition(), CancellationToken.None);

        Assert.Equal(SharedProjectPublishOutcome.PermissionDenied, result.Outcome);
        Assert.Equal("This action requires the Editor role on project 'new-project'.", result.Error);
    }

    [Fact]
    public async Task PublishAsync_TargetAlreadyHasADefinition_ReturnsAlreadyPublishedAndNeverWrites()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "existing", _DefinitionEnvelope("""{"schemaVersion":1,"name":"Existing"}"""));

        var result = await SourceFor(host).PublishAsync($"{scheme}:existing", Definition(), CancellationToken.None);

        Assert.Equal(SharedProjectPublishOutcome.AlreadyPublished, result.Outcome);
        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_ReadFailsForAReasonOtherThanNotFound_ReportsFailedAndNeverWrites()
    {
        // A permission or connectivity failure must never be read as "safe to publish" — only Depot's own
        // documented [NotFound] wording clears the way to write.
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "flaky", PluginMcpToolCallResult.Failed("connection reset"));

        var result = await SourceFor(host).PublishAsync($"{scheme}:flaky", Definition(), CancellationToken.None);

        Assert.Equal(SharedProjectPublishOutcome.Failed, result.Outcome);
        Assert.Equal("connection reset", result.Error);
        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_NotSignedIn_ReportsFailedAndNeverWrites()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "new-project", PluginMcpToolCallResult.AuthorizationRequired);

        var result = await SourceFor(host).PublishAsync($"{scheme}:new-project", Definition(), CancellationToken.None);

        Assert.Equal(SharedProjectPublishOutcome.Failed, result.Outcome);
        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_WrongScheme_FailsWithoutCallingDepot()
    {
        var host = Substitute.For<ICockpitHost>();

        var result = await SourceFor(host).PublishAsync("other-scheme:new-project", Definition(), CancellationToken.None);

        Assert.Equal(SharedProjectPublishOutcome.Failed, result.Outcome);
        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // AC-620's own DoD: a secret-shaped resource row must never reach Depot unencrypted. Measures the actual wire
    // JSON PublishAsync sends, not the intent — the same bar CockpitProjectDefinitionSecrecyTests already sets for
    // the write path this call shares.
    [Fact]
    public async Task PublishAsync_SecretShapedResourceRow_NeverReachesTheWrittenDefinition()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "new-project", _NotFound("new-project"));
        var written = _StubWriteCapturingRawContent(host, "new-project", PluginMcpToolCallResult.Success("""{"checksum":"chk1"}"""));

        var resources = new List<SharedProjectPublishResource>
        {
            new("Instructions", "docs/RUNBOOK.md", "Runbook"),
            new("Credential", "~/.ssh/id_rsa", "Deploy key"),
        };

        var result = await SourceFor(host).PublishAsync($"{scheme}:new-project", Definition(resources: resources), CancellationToken.None);

        Assert.Equal(SharedProjectPublishOutcome.Success, result.Outcome);
        var sentJson = written();
        Assert.DoesNotContain("id_rsa", sentJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy key", sentJson, StringComparison.Ordinal);
        Assert.Contains("RUNBOOK.md", sentJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListPublishTargetsAsync_KeepsOnlyEditorAndOwnerRoles_NotViewerOrUnknown()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubListProjects(host, """
            {"projects":[
              {"slug":"can-write","name":"Can write","role":"Editor","kind":"Project"},
              {"slug":"also-owner","name":"Also owner","role":"Owner","kind":"Project"},
              {"slug":"read-only","name":"Read only","role":"Viewer","kind":"Project"},
              {"slug":"unknown-role","name":"Unknown role","role":"Weird","kind":"Project"}
            ]}
            """);

        var result = await SourceFor(host).ListPublishTargetsAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            new[] { $"{scheme}:can-write", $"{scheme}:also-owner" }.OrderBy(id => id, StringComparer.Ordinal),
            result.Targets.Select(target => target.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    // AC-699: the exact list_projects payload depot.krahwinkel-it.nl returned for the operator who reported this
    // (13 projects, every role "Admin", three of them Brains) — the picker was empty against this very response.
    [Fact]
    public async Task ListPublishTargetsAsync_TheReportedServersOwnResponse_FillsThePicker()
    {
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, """
            {"projects":[
              {"slug":"ai-hub","name":"AI-Hub","role":"Admin","kind":"Project"},
              {"slug":"ashenmoon","name":"Ashenmoon","role":"Admin","kind":"Project"},
              {"slug":"cockpit","name":"Cockpit","role":"Admin","kind":"Project"},
              {"slug":"depot","name":"Depot","role":"Admin","kind":"Project"},
              {"slug":"eve-together","name":"EVE Together","role":"Admin","kind":"Project"},
              {"slug":"eve-workbench","name":"EVE Workbench","role":"Admin","kind":"Project"},
              {"slug":"kontena","name":"Kontena","role":"Admin","kind":"Project"},
              {"slug":"olaf","name":"Olaf","role":"Admin","kind":"Brain"},
              {"slug":"sql-explorer","name":"SQL Explorer","role":"Admin","kind":"Project"},
              {"slug":"startpage","name":"Startpage","role":"Admin","kind":"Project"},
              {"slug":"synvolution-flow","name":"Synvolution Flow","role":"Admin","kind":"Project"},
              {"slug":"testy","name":"Testy","role":"Admin","kind":"Brain"},
              {"slug":"vex","name":"Vex","role":"Admin","kind":"Brain"}
            ]}
            """);

        var result = await SourceFor(host).ListPublishTargetsAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(10, result.Targets.Count);
        Assert.DoesNotContain(result.Targets, target => target.Name is "Olaf" or "Testy" or "Vex");
    }

    // AC-699, measured against a real server: Depot reports "Admin" for every project a global admin can see
    // (ListProjectsForUserQuery), which this filter used to read as Unknown — emptying the whole publish dropdown
    // for exactly the operator most likely to publish.
    [Fact]
    public async Task ListPublishTargetsAsync_AdminRole_IsAPublishTargetAndShowsItsRole()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubListProjects(host, """{"projects":[{"slug":"cockpit","name":"Cockpit","role":"Admin","kind":"Project"}]}""");

        var result = await SourceFor(host).ListPublishTargetsAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        var target = Assert.Single(result.Targets);
        Assert.Equal($"{scheme}:cockpit", target.Id);
        Assert.Equal("Admin", target.Role);
    }

    [Fact]
    public async Task ListPublishTargetsAsync_ExcludesBrainKind()
    {
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, """{"projects":[{"slug":"notes","name":"Notes","role":"Owner","kind":"Brain"}]}""");

        var result = await SourceFor(host).ListPublishTargetsAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Targets);
    }

    [Fact]
    public async Task ListPublishTargetsAsync_IncludesTargetsWithNoExistingDefinitionYet()
    {
        // The defining difference from ListAsync: a brand-new Depot project with no .cockpit/project.json at all
        // is exactly the common publish target, not something to filter out.
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubListProjects(host, """{"projects":[{"slug":"brand-new","name":"Brand new","role":"Owner","kind":"Project"}]}""");

        var result = await SourceFor(host).ListPublishTargetsAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal($"{scheme}:brand-new", Assert.Single(result.Targets).Id);
    }
}
