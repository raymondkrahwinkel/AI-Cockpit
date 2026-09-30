using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSharedProjectSource.WriteBackAsync` (AC-247): the operator's edit to a bound project's claimed fields,
// landing back in Depot. Every fixture is the actual JSON text Depot's `read`/`write` tools would send, per
// the "measure against a real-looking response" discipline `DepotSharedProjectSourcePrepareBindingTests` sets.
public class DepotSharedProjectSourceWriteBackTests
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

    private static SharedProjectDefinitionEdit Edit(
        string name = "Edited name", string? description = "Edited description", SharedProjectLogoEdit? logoEdit = null) =>
        new(name, description, BehaviorPrompt: "Edited behaviour", IsolateInWorktreeByDefault: true, EnabledMcpServerNames: ["github"], LogoEdit: logoEdit);

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
    public async Task WriteBackAsync_APlaceholderResourceRow_SurvivesTheRoundTripRatherThanBeingDropped()
    {
        // SharedProjectBinding's read shape blanks a placeholder row's Reference (AC-246 idiom); reconstructing
        // resources from that shape would hand Create a blank reference and silently drop the row. WriteBackAsync
        // must instead carry the pre-write read's own Resources list through unchanged.
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        var reference = OperatingSystem.IsWindows() ? @"C:\Users\erik\work\notes.md" : "/home/erik/work/notes.md";
        var placeholder = CockpitProjectResourceEntry.Create("Reference", reference, "Notes")!;
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
    public async Task WriteBackAsync_AnIdBelongingToADifferentConnection_FailsWithoutCallingDepotAtAll()
    {
        var host = Substitute.For<ICockpitHost>();

        var result = await SourceFor(host).WriteBackAsync("some-other-scheme:cockpit", Edit(), "chk-before", CancellationToken.None);

        Assert.Equal(SharedProjectWriteBackOutcome.Failed, result.Outcome);
        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
