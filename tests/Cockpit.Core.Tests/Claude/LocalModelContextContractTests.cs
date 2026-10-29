using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Core.Tests.Claude;

internal static class LocalModelContextContract
{
    internal static async Task AssertAsync()
    {
        _ProfileContextRoundTripsAndDrivesTheTurnGuard();
        await _OllamaDoesNotFakeNumCtxPerRequest();
        await _LmStudioLoadsAnUnloadedModel();
        await _LmStudioReloadsASmallerContextWhenEnabled();
        await _LmStudioWarnsWithoutReloadWhenDisabled();
        await _LmStudioMissingV1ApiOnlyWarns();
    }

    private static void _ProfileContextRoundTripsAndDrivesTheTurnGuard()
    {
        var stored = ProviderConfigEntry.FromDomain(new LmStudioConfig("http://localhost:1234", "qwen", ContextTokens: 65_536, LoadModelWithContext: true));
        var loaded = Assert.IsType<LmStudioConfig>(stored.ToDomain(string.Empty, null));

        Assert.Equal(LocalModelContext.DefaultTokens, new OllamaConfig("http://localhost:11434", "qwen").ContextTokens);
        Assert.Equal(65_536, loaded.ContextTokens);
        Assert.True(loaded.LoadModelWithContext);
        Assert.Equal(65_536, OpenAiCompatSessionDriver.TurnLimitsFrom(loaded).ContextWindowTokens);
        Assert.Contains("Profiles[].Provider.ContextTokens", SettingsCarrierWhitelist.Carried);
        Assert.Contains("Profiles[].Provider.LoadModelWithContext", SettingsCarrierWhitelist.Carried);

        var protectedSettings = JsonNode.Parse("""{"ContextTokens":32768,"ApiKey":"secret"}""")!.AsObject();
        Assert.Equal(
            ["ApiKey"],
            SecretJsonWalker.Transform(protectedSettings, SecretFields.ByName, (_, _) => "protected"));
        Assert.Equal(32_768, protectedSettings["ContextTokens"]!.GetValue<int>());

        var recovered = JsonSerializer.Deserialize<ProviderConfigEntry>(
            """{"Provider":"Ollama","ContextTokens":"32768"}""",
            CockpitConfigFileAccess.SerializerOptions);
        Assert.Equal(32_768, recovered!.ContextTokens);
    }

    private static async Task _OllamaDoesNotFakeNumCtxPerRequest()
    {
        var handler = new RecordingHandler(_ => _Json("{}"));
        var warning = await _Manager(handler).PrepareAsync(
            new OllamaConfig("http://localhost:11434", "qwen", ContextTokens: 32_768));

        Assert.Contains("OLLAMA_CONTEXT_LENGTH", warning);
        Assert.Contains("PARAMETER num_ctx", warning);
        Assert.Empty(handler.Requests);
    }

    private static async Task _LmStudioLoadsAnUnloadedModel()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? _Json("""{"models":[{"key":"qwen","loaded_instances":[]}]}""")
            : _Json("{}"));
        var manager = _Manager(handler);

        var warning = await manager.PrepareAsync(new LmStudioConfig("http://localhost:1234", "qwen", ContextTokens: 32_768));

        Assert.Null(warning);
        Assert.Equal(["/api/v1/models", "/api/v1/models/load"], handler.Requests.Select(request => request.Path));
        Assert.Contains("\"model\":\"qwen\"", handler.Requests[1].Body);
        Assert.Contains("\"context_length\":32768", handler.Requests[1].Body);
    }

    private static async Task _LmStudioReloadsASmallerContextWhenEnabled()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? _Json(_LoadedAt(8_192))
            : _Json("{}"));
        var manager = _Manager(handler);

        var warning = await manager.PrepareAsync(new LmStudioConfig("http://localhost:1234", "qwen", ContextTokens: 32_768, LoadModelWithContext: true));

        Assert.Null(warning);
        Assert.Equal(["/api/v1/models", "/api/v1/models/unload", "/api/v1/models/load"], handler.Requests.Select(request => request.Path));
        Assert.Contains("\"instance_id\":\"instance-1\"", handler.Requests[1].Body);
        Assert.Contains("\"model\":\"qwen\"", handler.Requests[2].Body);
        Assert.Contains("\"context_length\":32768", handler.Requests[2].Body);
    }

    private static async Task _LmStudioWarnsWithoutReloadWhenDisabled()
    {
        var handler = new RecordingHandler(_ => _Json(_LoadedAt(8_192)));
        var manager = _Manager(handler);

        var warning = await manager.PrepareAsync(new LmStudioConfig("http://localhost:1234", "qwen", ContextTokens: 32_768));

        Assert.Contains("geladen met 8192", warning);
        Assert.Contains("profiel vraagt 32768", warning);
        Assert.Single(handler.Requests);
    }

    private static async Task _LmStudioMissingV1ApiOnlyWarns()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var manager = _Manager(handler);

        var warning = await manager.PrepareAsync(new LmStudioConfig("http://localhost:1234", "qwen", ContextTokens: 32_768, LoadModelWithContext: true));

        Assert.Contains("v1 model-API", warning);
        Assert.Contains("32768", warning);
        Assert.Single(handler.Requests);
    }

    private static LocalModelContextManager _Manager(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<LocalModelContextManager>.Instance);

    private static string _LoadedAt(int contextTokens) =>
        "{\"models\":[{\"key\":\"qwen\",\"loaded_instances\":[{\"id\":\"instance-1\",\"config\":{\"context_length\":" + contextTokens + "}}]}]}";

    private static HttpResponseMessage _Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }
}
