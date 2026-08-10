using System.Reflection;
using System.Text.Json;
using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class CockpitProjectDefinitionStoreTests
{
    // AC-607 review finding 6: _WithGuardedData is a hand-written shallow copy that must be kept in sync if
    // CockpitProjectDefinition grows a property — this fails red if a future property is left at its default
    // instead of carried through, which the separate reflection-whitelist test (undeclared property) cannot catch.
    [Fact]
    public void WithGuardedData_RoundTripsEveryDefinitionPropertyUnchanged_WhenPassedBackWhatItAlreadyHad()
    {
        var definition = new CockpitProjectDefinition
        {
            SchemaVersion = 99,
            Name = "probe",
            Description = "d",
            GitUrl = "g",
            BehaviorPrompt = "b",
            IsolateInWorktreeByDefault = true,
            McpOverlay = new CockpitProjectMcpOverlayEntry { Enabled = ["server"] },
            Resources = [new CockpitProjectResourceEntry { Role = "memory", Reference = "ref" }],
            Logo = "logo.png",
            SensitiveFields = [new CockpitProjectSensitiveFieldEntry { Label = "L", Value = "enc:v1:AAAA" }],
            PasswordEnvelope = new CockpitProjectPasswordEnvelope(),
            ExtensionData = new Dictionary<string, JsonElement> { ["x"] = JsonSerializer.SerializeToElement("y") },
        };

        var method = typeof(CockpitProjectDefinitionStore).GetMethod("_WithGuardedData", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("_WithGuardedData not found via reflection.");
        var result = method.Invoke(null, [definition, definition.ExtensionData, definition.SensitiveFields]) as CockpitProjectDefinition
            ?? throw new InvalidOperationException("_WithGuardedData did not return a CockpitProjectDefinition.");

        foreach (var property in typeof(CockpitProjectDefinition).GetProperties())
        {
            Assert.Equal(property.GetValue(definition), property.GetValue(result));
        }
    }

    // AC-607 decision 3: WriteAsync must never send a secret-shaped, not-already-encrypted ExtensionData field to
    // Depot, and must report it dropped. Rood-zonder-fix already proved on CockpitProjectDefinitionExtensionDataGuard
    // itself; this proves the guard is actually wired into the write path, not merely available.
    [Fact]
    public async Task WriteAsync_ExtensionDataHasSecretShapedPlaintextField_DropsItBeforeSendingAndReportsIt()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"path":"x","checksum":"c1","bytesWritten":1}""")));

        var definition = new CockpitProjectDefinition
        {
            Name = "probe",
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["newerSecretToken"] = JsonSerializer.SerializeToElement("plaintext-leak"),
            },
        };

        var result = await CockpitProjectDefinitionStore.WriteAsync(host, "Depot: Acme", "cockpit", definition, baseChecksum: null);

        Assert.Equal(["newerSecretToken"], result.DroppedExtensionKeys);
        await host.Received(1).CallMcpToolAsync(
            "Depot: Acme", "write",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => !((string)args!["content"]!).Contains("plaintext-leak", StringComparison.Ordinal)),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        // The caller's own object must be untouched — WriteAsync guards a copy, never the definition it was given.
        Assert.True(definition.ExtensionData.ContainsKey("newerSecretToken"));
    }

    [Fact]
    public async Task WriteAsync_ExtensionDataHasSecretShapedFieldAlreadyEncrypted_PassesItThroughAndReportsNoDrop()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"path":"x","checksum":"c1","bytesWritten":1}""")));

        var definition = new CockpitProjectDefinition
        {
            Name = "probe",
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["newerSecretToken"] = JsonSerializer.SerializeToElement("enc:v1:AAAA"),
            },
        };

        var result = await CockpitProjectDefinitionStore.WriteAsync(host, "Depot: Acme", "cockpit", definition, baseChecksum: null);

        Assert.Null(result.DroppedExtensionKeys);
        await host.Received(1).CallMcpToolAsync(
            "Depot: Acme", "write",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => ((string)args!["content"]!).Contains("enc:v1:AAAA", StringComparison.Ordinal)),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // AC-607 review finding 4: the guard must also reach a SensitiveFields row's own ExtensionData, not only the
    // definition's top-level one.
    [Fact]
    public async Task WriteAsync_SensitiveFieldRowHasSecretShapedPlaintextFallbackField_DropsItBeforeSendingAndReportsIt()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"path":"x","checksum":"c1","bytesWritten":1}""")));

        var definition = new CockpitProjectDefinition
        {
            Name = "probe",
            SensitiveFields =
            [
                new CockpitProjectSensitiveFieldEntry
                {
                    Label = "Deploy token",
                    Value = "enc:v1:AAAA",
                    ExtensionData = new Dictionary<string, JsonElement>
                    {
                        ["fallbackPassword"] = JsonSerializer.SerializeToElement("plaintext-leak"),
                    },
                },
            ],
        };

        var result = await CockpitProjectDefinitionStore.WriteAsync(host, "Depot: Acme", "cockpit", definition, baseChecksum: null);

        Assert.Equal(["SensitiveFields.Deploy token.fallbackPassword"], result.DroppedExtensionKeys);
        await host.Received(1).CallMcpToolAsync(
            "Depot: Acme", "write",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => !((string)args!["content"]!).Contains("plaintext-leak", StringComparison.Ordinal)),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        // The caller's own object must be untouched, same rule as for the top-level guard.
        Assert.True(definition.SensitiveFields[0].ExtensionData!.ContainsKey("fallbackPassword"));
    }

    [Fact]
    public async Task ReadAsync_Success_CallsReadWithTheReservedPathAndParsesTheDefinition()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync("Depot: Acme", "read", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success(
                """{"path":".cockpit/project.json","content":"{\"schemaVersion\":1,\"name\":\"probe\"}","checksum":"abc123","size":30}""")));

        var result = await CockpitProjectDefinitionStore.ReadAsync(host, "Depot: Acme", "cockpit");

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        Assert.Equal("probe", result.Definition!.Name);
        Assert.Equal("abc123", result.Checksum);
        await host.Received(1).CallMcpToolAsync(
            "Depot: Acme", "read",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => (string)args!["project"]! == "cockpit" && (string)args["path"]! == ".cockpit/project.json"),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadAsync_NotSignedIn_ReportsAuthorizationRequired()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.AuthorizationRequired));

        var result = await CockpitProjectDefinitionStore.ReadAsync(host, "Depot: Acme", "cockpit");

        Assert.Equal(PluginMcpToolCallOutcome.AuthorizationRequired, result.Outcome);
        Assert.Null(result.Definition);
    }

    [Fact]
    public async Task ReadAsync_ToolCallFails_ReportsFailedWithTheServersOwnMessage()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("not found")));

        var result = await CockpitProjectDefinitionStore.ReadAsync(host, "Depot: Acme", "cockpit");

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Equal("not found", result.Error);
    }

    [Fact]
    public async Task ReadAsync_ContentIsNotValidDefinitionJson_ReportsFailedRatherThanThrowing()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"path":"x","content":"not json","checksum":"abc"}""")));

        var result = await CockpitProjectDefinitionStore.ReadAsync(host, "Depot: Acme", "cockpit");

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ReadAsync_EnvelopeMissingChecksum_ReportsFailedRatherThanThrowing()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"path":"x","content":"{}"}""")));

        var result = await CockpitProjectDefinitionStore.ReadAsync(host, "Depot: Acme", "cockpit");

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task WriteAsync_BaseChecksumProvided_IsPassedToTheWriteTool()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"path":".cockpit/project.json","checksum":"new123","bytesWritten":10}""")));

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Acme", "cockpit", new CockpitProjectDefinition { Name = "probe" }, baseChecksum: "old123");

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        Assert.Equal("new123", result.Checksum);
        await host.Received(1).CallMcpToolAsync(
            "Depot: Acme", "write",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => (string)args!["baseChecksum"]! == "old123"),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteAsync_NoBaseChecksum_OmitsItFromTheArguments()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"path":"x","checksum":"first","bytesWritten":1}""")));

        await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Acme", "cockpit", new CockpitProjectDefinition { Name = "probe" }, baseChecksum: null);

        await host.Received(1).CallMcpToolAsync(
            "Depot: Acme", "write",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => !args!.ContainsKey("baseChecksum")),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteAsync_ChecksumMismatch_ClassifiesAsChecksumConflict()
    {
        // The exact sentence Depot's own WriteFileCommandHandler sends back on a baseChecksum mismatch — measured
        // live against a real Depot server (AC-247), not guessed: write, read, write-with-correct-baseChecksum,
        // then a stale-baseChecksum write was rejected (content unchanged on re-read) and cross-checked against
        // Depot's own source (Depot.Application/Modules/Storage/Commands/WriteFile/WriteFileCommand.cs).
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed(
                "'.cockpit/project.json' changed since it was read; current checksum is abc999. Re-read and retry.")));

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Acme", "cockpit", new CockpitProjectDefinition { Name = "probe" }, baseChecksum: "stale");

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.ChecksumConflict, result.FailureKind);
    }

    [Fact]
    public async Task WriteAsync_RoleBelowEditor_ClassifiesAsPermissionDenied()
    {
        // Depot's own ProjectMemberAccessGuard phrasing for a role-too-low write (source:
        // Depot.Infrastructure/Access/ProjectMemberAccessGuard.cs) — this store's own callerRole pre-check (below)
        // never reaches Depot at all, so this is the case where Depot's own enforcement is the only line of defense
        // (e.g. a stale/unsupplied callerRole).
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("This action requires the Editor role on project 'cockpit'.")));

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Acme", "cockpit", new CockpitProjectDefinition { Name = "probe" }, baseChecksum: null);

        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.PermissionDenied, result.FailureKind);
    }

    [Fact]
    public async Task WriteAsync_NotAProjectMember_ClassifiesAsPermissionDenied()
    {
        // Depot's non-member phrasing, same source as above.
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("You are not a member of project 'cockpit'.")));

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Acme", "cockpit", new CockpitProjectDefinition { Name = "probe" }, baseChecksum: null);

        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.PermissionDenied, result.FailureKind);
    }

    [Fact]
    public async Task WriteAsync_UnrelatedFailure_ClassifiesAsUnclassified()
    {
        // A failure whose text matches neither known Depot shape must not be guessed into either bucket.
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("Could not connect to \"Depot: Acme\".")));

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Acme", "cockpit", new CockpitProjectDefinition { Name = "probe" }, baseChecksum: null);

        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.Unclassified, result.FailureKind);
    }

    [Fact]
    public async Task WriteAsync_CallerRoleIsViewer_NeverCallsDepotAndNamesTheReason()
    {
        var host = Substitute.For<ICockpitHost>();

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Acme", "cockpit", new CockpitProjectDefinition { Name = "probe" },
            baseChecksum: "abc", callerRole: CockpitProjectRole.Viewer);

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.PermissionDenied, result.FailureKind);
        Assert.NotNull(result.Error);
        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CockpitProjectRole.Editor)]
    [InlineData(CockpitProjectRole.Owner)]
    public async Task WriteAsync_CallerRoleCanWrite_ProceedsToCallDepot(CockpitProjectRole role)
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"path":"x","checksum":"c1","bytesWritten":1}""")));

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Acme", "cockpit", new CockpitProjectDefinition { Name = "probe" },
            baseChecksum: "abc", callerRole: role);

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        await host.Received(1).CallMcpToolAsync(
            Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteAsync_NotSignedIn_ReportsAuthorizationRequired()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.AuthorizationRequired));

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Acme", "cockpit", new CockpitProjectDefinition { Name = "probe" }, baseChecksum: null);

        Assert.Equal(PluginMcpToolCallOutcome.AuthorizationRequired, result.Outcome);
    }
}
