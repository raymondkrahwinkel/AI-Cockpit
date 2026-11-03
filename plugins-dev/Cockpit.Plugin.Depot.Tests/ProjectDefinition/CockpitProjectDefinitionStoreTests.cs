using System.Net;
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
        var kept = definition.SensitiveFields[0].ExtensionData;
        Assert.NotNull(kept);
        Assert.True(kept.ContainsKey("fallbackPassword"));
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
    public async Task WriteAsync_CanWriteMissingAndCallerRoleIsViewer_DoesNotPutAndNamesTheReason()
    {
        var handler = new _StubHandler(HttpStatusCode.OK, """{"checksum":"c1"}""");

        var result = await _WriteAsync(
            handler, new CockpitProjectDefinition { Name = "probe" }, "abc", CockpitProjectRole.Viewer);

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.PermissionDenied, result.FailureKind);
        Assert.NotNull(result.Error);
        Assert.Equal(1, handler.CallCount);
    }

    private static async Task<CockpitProjectDefinitionWriteResult> _WriteAsync(
        _StubHandler handler, CockpitProjectDefinition definition, string? baseChecksum,
        CockpitProjectRole? callerRole = null)
    {
        handler.ProjectsContent = _Projects("""{"schemaVersion":1,"name":"probe"}""");
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
        public string? ProjectsContent { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            IfMatch = request.Headers.IfMatch.SingleOrDefault()?.Tag;
            IfNoneMatch = request.Headers.IfNoneMatch.SingleOrDefault()?.Tag;
            Path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Get && ProjectsContent is { } projectsContent)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(projectsContent),
                    RequestMessage = request,
                };
            }

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content),
                RequestMessage = request,
            };
        }
    }
}
