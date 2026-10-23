using System.Net;
using System.Reflection;
using System.Text.Json;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class CockpitProjectDefinitionStoreTests
{
    private static readonly DepotConnectionRegistration _Connection =
        new("c1", "Acme", "https://depot.example.com");

    // AC-607 review finding 6: _WithGuardedData is a hand-written shallow copy that must be kept in sync if
    // CockpitProjectDefinition grows a property.
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

    [Fact]
    public async Task WriteAsync_ExtensionDataHasSecretShapedPlaintextField_DropsItBeforeSendingAndReportsIt()
    {
        var handler = new _StubHandler(HttpStatusCode.OK, """{"checksum":"c1"}""");
        var definition = new CockpitProjectDefinition
        {
            Name = "probe",
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["newerSecretToken"] = JsonSerializer.SerializeToElement("plaintext-leak"),
            },
        };

        var result = await _WriteAsync(handler, definition, baseChecksum: null);

        Assert.Equal(["newerSecretToken"], result.DroppedExtensionKeys);
        Assert.DoesNotContain("plaintext-leak", handler.Body, StringComparison.Ordinal);
        Assert.Equal("*", handler.IfNoneMatch);
        Assert.True(definition.ExtensionData.ContainsKey("newerSecretToken"));
    }

    [Fact]
    public async Task WriteAsync_ExtensionDataHasSecretShapedFieldAlreadyEncrypted_PassesItThroughAndReportsNoDrop()
    {
        var handler = new _StubHandler(HttpStatusCode.OK, """{"checksum":"c1"}""");
        var definition = new CockpitProjectDefinition
        {
            Name = "probe",
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["newerSecretToken"] = JsonSerializer.SerializeToElement("enc:v1:AAAA"),
            },
        };

        var result = await _WriteAsync(handler, definition, baseChecksum: null);

        Assert.Null(result.DroppedExtensionKeys);
        Assert.Contains("enc:v1:AAAA", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_SensitiveFieldRowHasSecretShapedPlaintextFallbackField_DropsItBeforeSendingAndReportsIt()
    {
        var handler = new _StubHandler(HttpStatusCode.OK, """{"checksum":"c1"}""");
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

        var result = await _WriteAsync(handler, definition, baseChecksum: null);

        Assert.Equal(["SensitiveFields.Deploy token.fallbackPassword"], result.DroppedExtensionKeys);
        Assert.DoesNotContain("plaintext-leak", handler.Body, StringComparison.Ordinal);
        Assert.True(definition.SensitiveFields[0].ExtensionData!.ContainsKey("fallbackPassword"));
    }

    [Fact]
    public async Task ReadAsync_Success_UsesTheInlineProjectDefinition()
    {
        var handler = new _StubHandler(HttpStatusCode.OK, _Projects("""{"schemaVersion":1,"name":"probe"}"""));
        using var http = new HttpClient(handler);

        var result = await CockpitProjectDefinitionStore.ReadAsync(
            Substitute.For<ICockpitHost>(), _Connection, "acme/cockpit", http);

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        Assert.Equal("probe", result.Definition!.Name);
        Assert.Equal("abc123", result.Checksum);
        Assert.Equal("/cockpit/projects", handler.Path);
    }

    [Fact]
    public async Task ReadAsync_ContentIsNotValidDefinitionJson_ReportsFailedRatherThanThrowing()
    {
        var handler = new _StubHandler(HttpStatusCode.OK, _Projects("""{"schemaVersion":"bad"}"""));
        using var http = new HttpClient(handler);

        var result = await CockpitProjectDefinitionStore.ReadAsync(
            Substitute.For<ICockpitHost>(), _Connection, "acme/cockpit", http);

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task WriteAsync_BaseChecksumProvided_IsSentAsIfMatch()
    {
        var handler = new _StubHandler(HttpStatusCode.OK, """{"checksum":"new123"}""");

        var result = await _WriteAsync(handler, new CockpitProjectDefinition { Name = "probe" }, "old123");

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        Assert.Equal("new123", result.Checksum);
        Assert.Equal("\"old123\"", handler.IfMatch);
        Assert.Equal("/cockpit/projects/acme/cockpit/definition", handler.Path);
    }

    [Fact]
    public void Failed_ChecksumMismatchText_ClassifiesAsChecksumConflict()
    {
        var result = CockpitProjectDefinitionWriteResult.Failed(
            "'.cockpit/project.json' changed since it was read; current checksum is abc999. Re-read and retry.");

        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.ChecksumConflict, result.FailureKind);
    }

    // AC-1635: 409 is the fine-grained conflict row; the journey covers Depot's 412 response end to end.
    [Fact]
    public async Task WriteAsync_StaleChecksum_MapsHttpConflictWithoutOverwriting()
    {
        var handler = new _StubHandler(HttpStatusCode.Conflict);

        var result = await _WriteAsync(handler, new CockpitProjectDefinition { Name = "stale edit" }, "stale");

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.ChecksumConflict, result.FailureKind);
        Assert.Equal("\"stale\"", handler.IfMatch);
    }

    [Fact]
    public void Failed_NotAProjectMemberText_ClassifiesAsPermissionDenied()
    {
        var result = CockpitProjectDefinitionWriteResult.Failed(
            "You are not a member of project 'cockpit'.");

        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.PermissionDenied, result.FailureKind);
    }

    [Fact]
    public async Task WriteAsync_CallerRoleIsViewer_NeverCallsDepotAndNamesTheReason()
    {
        var handler = new _StubHandler(HttpStatusCode.OK, """{"checksum":"c1"}""");

        var result = await _WriteAsync(
            handler, new CockpitProjectDefinition { Name = "probe" }, "abc", CockpitProjectRole.Viewer);

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.PermissionDenied, result.FailureKind);
        Assert.NotNull(result.Error);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task WriteAsync_Forbidden_ClassifiesAsPermissionDenied()
    {
        var handler = new _StubHandler(HttpStatusCode.Forbidden);

        var result = await _WriteAsync(handler, new CockpitProjectDefinition { Name = "probe" }, baseChecksum: null);

        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.PermissionDenied, result.FailureKind);
    }

    [Fact]
    public async Task WriteAsync_NotSignedIn_ReportsAuthorizationRequired()
    {
        var handler = new _StubHandler(HttpStatusCode.Unauthorized);

        var result = await _WriteAsync(handler, new CockpitProjectDefinition { Name = "probe" }, baseChecksum: null);

        Assert.Equal(PluginMcpToolCallOutcome.AuthorizationRequired, result.Outcome);
    }

    private static async Task<CockpitProjectDefinitionWriteResult> _WriteAsync(
        _StubHandler handler, CockpitProjectDefinition definition, string? baseChecksum,
        CockpitProjectRole? callerRole = null)
    {
        using var http = new HttpClient(handler);
        return await CockpitProjectDefinitionStore.WriteAsync(
            Substitute.For<ICockpitHost>(), _Connection, "acme/cockpit", definition, baseChecksum,
            callerRole, http);
    }

    private static string _Projects(string definition) =>
        """{"projects":[{"address":"acme/cockpit","slug":"cockpit","name":"Cockpit","role":"Editor","kind":"Project","organization":{"slug":"acme","name":"Acme","role":"Member"},"cockpit":{"state":"ok","checksum":"abc123","updatedAt":"2026-10-23T12:00:00Z","definition":"""
        + definition
        + """}}]}""";

    private sealed class _StubHandler(HttpStatusCode statusCode, string content = "") : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? Body { get; private set; }
        public string? IfMatch { get; private set; }
        public string? IfNoneMatch { get; private set; }
        public string? Path { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            IfMatch = request.Headers.IfMatch.SingleOrDefault()?.Tag;
            IfNoneMatch = request.Headers.IfNoneMatch.SingleOrDefault()?.Tag;
            Path = request.RequestUri?.AbsolutePath;
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content),
                RequestMessage = request,
            };
        }
    }
}
