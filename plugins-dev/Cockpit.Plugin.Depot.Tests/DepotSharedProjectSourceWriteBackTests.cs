using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSharedProjectSource.WriteBackAsync` (AC-247): the operator's edit to a bound project's claimed fields,
// landing back in Depot. Every fixture is the actual JSON text Depot's `read`/`write` tools would send, the same
// "measure against a real-looking response" discipline `DepotSharedProjectSourcePrepareBindingTests` already
// documents.
public class DepotSharedProjectSourceWriteBackTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Work", "https://depot.example.com");

    private static ISharedProjectSource SourceFor(ICockpitHost host) =>
        DepotMemorySource.BuildSharedProjectSources([Connection()], host).Single();

    private static string _Scheme(ICockpitHost host) =>
        DepotMemorySource.BuildRegistrationPairs([Connection()], host).Single().Registration.Scheme;

    private static PluginMcpToolCallResult _ReadEnvelope(string definitionJson, string checksum = "chk-before") =>
        PluginMcpToolCallResult.Success(
            $$"""{"path":".cockpit/project.json","content":{{System.Text.Json.JsonSerializer.Serialize(definitionJson)}},"checksum":"{{checksum}}","size":1}""");

    private static PluginMcpToolCallResult _WriteEnvelope(string checksum = "chk-after") =>
        PluginMcpToolCallResult.Success($$"""{"checksum":"{{checksum}}"}""");

    private static void _StubRead(ICockpitHost host, string slug, PluginMcpToolCallResult result) =>
        host.CallMcpToolAsync(
            Arg.Any<string>(), "read",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => args != null && (string)args["project"]! == slug),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));

    // Captures the `content` argument WriteBackAsync sends, so a test can assert on the merged definition's own
    // JSON rather than trusting a claim about it — the same discipline the DoD's own "measured, not guessed" bar
    // asks for everywhere else in this ticket.
    private static Func<CockpitProjectDefinition> _StubWriteCapturingContent(ICockpitHost host, string slug, PluginMcpToolCallResult result)
    {
        CockpitProjectDefinition? sent = null;
        host.CallMcpToolAsync(
            Arg.Any<string>(), "write",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => args != null && (string)args["project"]! == slug),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var arguments = callInfo.ArgAt<IReadOnlyDictionary<string, object?>?>(2)!;
                var json = (string)arguments["content"]!;
                Assert.True(CockpitProjectDefinitionJson.TryDeserialize(json, out var definition, out _));
                sent = definition;
                return Task.FromResult(result);
            });
        return () => sent ?? throw new InvalidOperationException("write was never called");
    }

    private static SharedProjectDefinitionEdit Edit(string name = "Edited name", string? description = "Edited description") =>
        new(name, description, BehaviorPrompt: "Edited behaviour", IsolateInWorktreeByDefault: true, EnabledMcpServerNames: ["github"]);

    [Fact]
    public async Task WriteBackAsync_Success_ReturnsTheChecksumTheWriteConfirmed()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "cockpit", _ReadEnvelope("""{"schemaVersion":1,"name":"Cockpit"}"""));
        _StubWriteCapturingContent(host, "cockpit", _WriteEnvelope("chk-after"));

        var result = await SourceFor(host).WriteBackAsync($"{scheme}:cockpit", Edit(), "chk-before", CancellationToken.None);

        Assert.Equal(SharedProjectWriteBackOutcome.Success, result.Outcome);
        Assert.Equal("chk-after", result.Checksum);
    }

    [Fact]
    public async Task WriteBackAsync_SendsTheOperatorsBaseChecksumRatherThanTheFreshReadsOwn()
    {
        // The whole point of optimistic concurrency: the write must be defended by the checksum the operator's
        // editor actually opened with, not by a checksum this call's own pre-write read just produced (which would
        // trivially always match Depot's current copy, since it is Depot's current copy).
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "cockpit", _ReadEnvelope("""{"schemaVersion":1,"name":"Cockpit"}""", checksum: "chk-fresh-read"));
        _StubWriteCapturingContent(host, "cockpit", _WriteEnvelope());

        await SourceFor(host).WriteBackAsync($"{scheme}:cockpit", Edit(), "chk-operator-opened-with", CancellationToken.None);

        await host.Received(1).CallMcpToolAsync(
            Arg.Any<string>(), "write",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => args != null && (string)args["baseChecksum"]! == "chk-operator-opened-with"),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteBackAsync_EditedFieldsLandInTheMergedDefinition()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "cockpit", _ReadEnvelope("""{"schemaVersion":1,"name":"Old name","description":"Old description"}"""));
        var sent = _StubWriteCapturingContent(host, "cockpit", _WriteEnvelope());

        await SourceFor(host).WriteBackAsync($"{scheme}:cockpit", Edit(name: "New name", description: "New description"), "chk-before", CancellationToken.None);

        Assert.Equal("New name", sent().Name);
        Assert.Equal("New description", sent().Description);
        Assert.Equal("Edited behaviour", sent().BehaviorPrompt);
        Assert.True(sent().IsolateInWorktreeByDefault);
        Assert.Equal(["github"], sent().McpOverlay!.Enabled);
    }

    [Fact]
    public async Task WriteBackAsync_GitUrlAndLogoSurviveUntouched()
    {
        // Neither field is part of SharedProjectDefinitionEdit (GitUrl is not claimable; Logo has no
        // artifact-upload path yet) — WriteBackAsync must carry both through from its own pre-write read rather
        // than dropping them because the edit does not mention them.
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "cockpit", _ReadEnvelope(
            """{"schemaVersion":1,"name":"Cockpit","gitUrl":"git@github.com:example/cockpit.git","logo":".cockpit/logo.png"}"""));
        var sent = _StubWriteCapturingContent(host, "cockpit", _WriteEnvelope());

        await SourceFor(host).WriteBackAsync($"{scheme}:cockpit", Edit(), "chk-before", CancellationToken.None);

        Assert.Equal("git@github.com:example/cockpit.git", sent().GitUrl);
        Assert.Equal(".cockpit/logo.png", sent().Logo);
    }

    [Fact]
    public async Task WriteBackAsync_APlaceholderResourceRow_SurvivesTheRoundTripRatherThanBeingDropped()
    {
        // The exact byte-fidelity risk this write path exists to avoid: SharedProjectBinding's own read shape
        // blanks a placeholder row's Reference (AC-246 idiom), so reconstructing resources from that shape would
        // hand CockpitProjectResourceEntry.Create a blank reference — which returns null, silently dropping the
        // row. WriteBackAsync must instead carry the pre-write read's own Resources list through unchanged.
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        var placeholder = CockpitProjectResourceEntry.Create("Reference", "/home/erik/work/notes.md", "Notes")!;
        Assert.True(placeholder.Placeholder);
        var written = CockpitProjectDefinitionJson.Serialize(new CockpitProjectDefinition { Name = "Cockpit", Resources = [placeholder] });
        _StubRead(host, "cockpit", _ReadEnvelope(written));
        var sent = _StubWriteCapturingContent(host, "cockpit", _WriteEnvelope());

        await SourceFor(host).WriteBackAsync($"{scheme}:cockpit", Edit(), "chk-before", CancellationToken.None);

        var resource = Assert.Single(sent().Resources!);
        Assert.True(resource.Placeholder);
        Assert.Equal("Notes", resource.Label);
        Assert.Equal(string.Empty, resource.Reference);
    }

    [Fact]
    public async Task WriteBackAsync_NullEnabledMcpServerNames_ClearsAnExistingRemoteOverlayRatherThanKeepingIt()
    {
        // Adversarial review finding: SharedProjectDefinitionEdit.EnabledMcpServerNames == null means "no
        // opinion, every server ticked" (the same idiom SharedProjectBinding's own read-direction property
        // documents) — the operator re-ticking every server to clear a remote restriction sends exactly this.
        // Falling back to current.McpOverlay on null would silently keep Depot's existing restriction instead.
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "cockpit", _ReadEnvelope("""{"schemaVersion":1,"name":"Cockpit","mcpOverlay":{"enabled":["github"]}}"""));
        var sent = _StubWriteCapturingContent(host, "cockpit", _WriteEnvelope());

        var edit = new SharedProjectDefinitionEdit("Cockpit", null, null, false, EnabledMcpServerNames: null);
        await SourceFor(host).WriteBackAsync($"{scheme}:cockpit", edit, "chk-before", CancellationToken.None);

        Assert.Null(sent().McpOverlay);
    }

    [Fact]
    public async Task WriteBackAsync_ChecksumConflict_ReturnsAFreshSnapshotFromThePreWriteRead()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "cockpit", _ReadEnvelope("""{"schemaVersion":1,"name":"Someone else's edit"}""", checksum: "chk-now"));
        host.CallMcpToolAsync(
            Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed(
                "'.cockpit/project.json' changed since it was read; current checksum is chk-now. Re-read and retry.")));

        var result = await SourceFor(host).WriteBackAsync($"{scheme}:cockpit", Edit(name: "My edit"), "chk-stale", CancellationToken.None);

        Assert.Equal(SharedProjectWriteBackOutcome.ChecksumConflict, result.Outcome);
        Assert.NotNull(result.LatestSnapshot);
        // The snapshot is Depot's own current state, not the caller's rejected edit — "My edit" must not leak in.
        Assert.Equal("Someone else's edit", result.LatestSnapshot!.Name);
        Assert.Equal("chk-now", result.LatestSnapshot.Checksum);
    }

    [Fact]
    public async Task WriteBackAsync_PermissionDenied_ReturnsTheServersOwnReason()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "cockpit", _ReadEnvelope("""{"schemaVersion":1,"name":"Cockpit"}"""));
        host.CallMcpToolAsync(
            Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("This action requires the Editor role on project 'cockpit'.")));

        var result = await SourceFor(host).WriteBackAsync($"{scheme}:cockpit", Edit(), "chk-before", CancellationToken.None);

        Assert.Equal(SharedProjectWriteBackOutcome.PermissionDenied, result.Outcome);
        Assert.Equal("This action requires the Editor role on project 'cockpit'.", result.Error);
    }

    [Fact]
    public async Task WriteBackAsync_AnUnrecognisedFailure_IsReportedAsFailedNotMisclassified()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "cockpit", _ReadEnvelope("""{"schemaVersion":1,"name":"Cockpit"}"""));
        host.CallMcpToolAsync(
            Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("Depot is down for maintenance.")));

        var result = await SourceFor(host).WriteBackAsync($"{scheme}:cockpit", Edit(), "chk-before", CancellationToken.None);

        Assert.Equal(SharedProjectWriteBackOutcome.Failed, result.Outcome);
        Assert.Equal("Depot is down for maintenance.", result.Error);
    }

    [Fact]
    public async Task WriteBackAsync_TheReadBeforeWriteFails_ReportsFailedWithoutAttemptingTheWrite()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "gone", PluginMcpToolCallResult.Failed("project not found"));

        var result = await SourceFor(host).WriteBackAsync($"{scheme}:gone", Edit(), "chk-before", CancellationToken.None);

        Assert.Equal(SharedProjectWriteBackOutcome.Failed, result.Outcome);
        Assert.Equal("project not found", result.Error);
        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteBackAsync_NotSignedInOnTheRead_ReportsASignInMessage()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        host.CallMcpToolAsync(Arg.Any<string>(), "read", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.AuthorizationRequired));

        var result = await SourceFor(host).WriteBackAsync($"{scheme}:cockpit", Edit(), "chk-before", CancellationToken.None);

        Assert.Equal(SharedProjectWriteBackOutcome.Failed, result.Outcome);
        Assert.Contains("Sign in", result.Error);
    }

    [Fact]
    public async Task WriteBackAsync_AnIdBelongingToADifferentConnection_FailsWithoutCallingDepotAtAll()
    {
        var host = Substitute.For<ICockpitHost>();

        var result = await SourceFor(host).WriteBackAsync("some-other-scheme:cockpit", Edit(), "chk-before", CancellationToken.None);

        Assert.Equal(SharedProjectWriteBackOutcome.Failed, result.Outcome);
        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
