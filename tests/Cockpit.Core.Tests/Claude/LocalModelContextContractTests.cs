using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Sessions;
using Cockpit.App.ViewModels;

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
        await _LmStudioExplicitOptionsAsync();
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

        var configured = new LmStudioConfig("http://localhost:1234", "qwen", ContextTokens: 32_768,
            LoadOptions: _EOptions, EnableThinking: true);
        var json = JsonSerializer.Serialize(ProviderConfigEntry.FromDomain(configured), CockpitConfigFileAccess.SerializerOptions);
        var restored = JsonSerializer.Deserialize<ProviderConfigEntry>(json, CockpitConfigFileAccess.SerializerOptions)!.ToDomain(string.Empty, null);
        Assert.Equal(configured, restored);
        Assert.DoesNotContain("ValidationError", json);
        var editor = new EditableProfileViewModel(new SessionProfile("E", configured), isLoggedIn: true);
        Assert.True(editor.ManageLmStudioLoadOptions);
        Assert.True(editor.IsValid);
        Assert.Equal(configured, editor.ToProfile().ProviderConfig);
        editor.LmStudioDraftModel = "draft";
        Assert.False(editor.IsValid);
        Assert.Contains("MTP", editor.LmStudioLoadOptionsError);
        editor.LmStudioDraftModel = string.Empty;
        editor.LmStudioGpuOffloadRatio = -1;
        Assert.False(editor.IsValid);
        editor.LmStudioGpuOffloadRatio = 1;
        editor.LmStudioFlashAttention = "Off";
        Assert.False(editor.IsValid);
        editor.ManageLmStudioLoadOptions = false;
        Assert.True(editor.IsValid);
        Assert.Null(Assert.IsType<LmStudioConfig>(editor.ToProfile().ProviderConfig).LoadOptions);
        var legacy = new EditableProfileViewModel(new SessionProfile("legacy", new LmStudioConfig("http://localhost:1234", "qwen")), true);
        Assert.Null(Assert.IsType<LmStudioConfig>(legacy.ToProfile().ProviderConfig).LoadOptions);
        Assert.Contains("Profiles[].Provider.LoadOptions", SettingsCarrierWhitelist.Carried);
        Assert.Contains("Profiles[].Provider.EnableThinking", SettingsCarrierWhitelist.Carried);
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.PrepareAsync(
            new LmStudioConfig("http://localhost:1234", "qwen", LoadOptions: _EOptions)));
    }

    private static readonly LmStudioLoadOptions _EOptions = new(
        GpuOffloadRatio: 1, Parallel: 1, FlashAttention: true,
        KCacheQuantization: "q8_0", VCacheQuantization: "q8_0", OffloadKvCacheToGpu: true,
        DraftMtp: true, DraftMaxTokens: 3, DraftMinTokens: 0, DraftMinContinueProbability: 0);

    private static async Task _LmStudioExplicitOptionsAsync()
    {
        var profile = new LmStudioConfig("http://localhost:1234", "qwen", ContextTokens: 32_768,
            ApiKey: "sk-lm-12345678:abcdefghijklmnopqrst", LoadOptions: _EOptions, EnableThinking: true);
        var fields = LmStudioModelOptions.Fields(profile);
        Assert.Equal(32_768, fields["llm.load.contextLength"]);
        Assert.Equal(1d, fields["llm.load.llama.acceleration.offloadRatio"]);
        Assert.False((bool)fields["llm.load.speculativeDecoding.draftSimple"]);
        Assert.DoesNotContain(fields.Keys, key => key.Contains("prediction", StringComparison.Ordinal));
        Assert.Null(_EOptions.ValidationError);
        Assert.NotNull((_EOptions with { GpuOffloadRatio = double.NaN }).ValidationError);
        Assert.NotNull((_EOptions with { Parallel = 0 }).ValidationError);
        Assert.NotNull((_EOptions with { KCacheQuantization = "invalid" }).ValidationError);
        Assert.NotNull((_EOptions with { DraftMinTokens = 4 }).ValidationError);
        Assert.NotNull((_EOptions with { DraftMinContinueProbability = double.PositiveInfinity }).ValidationError);

        var current = _SdkConfig(fields);
        using (var socket = new SdkSocket(current))
        {
            var summary = await new LmStudioModelOptions(socket).ReconcileAsync(profile, "instance-1", "physical-key", default);
            Assert.Contains("already loaded", summary);
            Assert.DoesNotContain(profile.ApiKey!, summary);
            Assert.Equal("12345678", socket.Packets[0].GetProperty("clientIdentifier").GetString());
            Assert.Single(socket.Packets, packet => packet.TryGetProperty("endpoint", out _));
        }

        foreach (var changed in fields.Keys)
        {
            var different = fields.ToDictionary(pair => pair.Key, pair => pair.Value);
            different[changed] = "different";
            using var socket = new SdkSocket(_SdkConfig(different));
            var summary = await new LmStudioModelOptions(socket).ReconcileAsync(profile, "instance-1", "physical-key", default);
            Assert.Contains("reloaded", summary);
            Assert.Equal(["getLoadConfig", "unloadModel", "loadModel", "getLoadConfig"],
                socket.Packets.Where(packet => packet.TryGetProperty("endpoint", out _)).Select(packet => packet.GetProperty("endpoint").GetString()));
            var load = Assert.Single(socket.Packets, packet => packet.TryGetProperty("type", out var type) && type.GetString() == "channelCreate");
            var creation = load.GetProperty("creationParameter");
            Assert.Equal("physical-key", creation.GetProperty("modelKey").GetString());
            Assert.Equal("qwen", creation.GetProperty("identifier").GetString());
            var layer = creation.GetProperty("loadConfigStack").GetProperty("layers")[0];
            Assert.Equal("apiOverride", layer.GetProperty("layerName").GetString());
            Assert.True(LmStudioModelOptions.Matches(layer.GetProperty("config"), fields));
        }

        using (var socket = new SdkSocket(_SdkConfig([])))
        {
            var summary = await new LmStudioModelOptions(socket).ReconcileAsync(profile, null, profile.Model, default);
            Assert.Contains(": loaded;", summary);
            Assert.DoesNotContain(socket.Packets, packet => packet.TryGetProperty("endpoint", out var endpoint) && endpoint.GetString() == "unloadModel");
        }

        foreach (var failure in new[] { "auth", "unload", "load", "verification" })
        {
            using var socket = new SdkSocket(_SdkConfig([])) { Failure = failure };
            await Assert.ThrowsAsync<InvalidOperationException>(() => new LmStudioModelOptions(socket).ReconcileAsync(profile, "instance-1", profile.Model, default));
            if (failure is "auth" or "unload")
            {
                Assert.DoesNotContain(socket.Packets, packet => packet.TryGetProperty("type", out var type) && type.GetString() == "channelCreate");
            }
        }

        var draft = profile with { LoadOptions = new LmStudioLoadOptions(DraftModel: "draft-key") };
        var draftFields = LmStudioModelOptions.Fields(draft);
        Assert.True((bool)draftFields["llm.load.speculativeDecoding.draftSimple"]);
        Assert.False((bool)draftFields["llm.load.speculativeDecoding.draftMtp"]);
        Assert.False(draftFields.ContainsKey("llm.load.llama.flashAttention"));
        Assert.False(draftFields.ContainsKey("llm.load.llama.kCacheQuantizationType"));
        Assert.False(draftFields.ContainsKey("llm.load.llama.acceleration.offloadRatio"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var cancelledSocket = new SdkSocket(current);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LmStudioModelOptions(cancelledSocket).ReconcileAsync(profile, null, profile.Model, cancelled.Token));
    }

    private static JsonElement _SdkConfig(IEnumerable<KeyValuePair<string, object>> fields) =>
        JsonSerializer.SerializeToElement(new { fields = fields.Select(pair => new { key = pair.Key, value = pair.Value }) });

    private sealed class SdkSocket(JsonElement config) : WebSocket
    {
        private readonly Queue<byte[]> _responses = new();
        private byte[]? _receiving;
        private int _offset;
        internal List<JsonElement> Packets { get; } = [];
        internal string? Failure { get; init; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(buffer.AsMemory());
            var packet = document.RootElement.Clone();
            Packets.Add(packet);
            void Reply(object response) => _responses.Enqueue(JsonSerializer.SerializeToUtf8Bytes(response));
            if (packet.TryGetProperty("authVersion", out _))
            {
                Reply(new { success = Failure != "auth" });
            }
            else if (packet.GetProperty("type").GetString() == "rpcCall")
            {
                var callId = packet.GetProperty("callId").GetInt32();
                var endpoint = packet.GetProperty("endpoint").GetString();
                if (endpoint == "unloadModel" && Failure == "unload")
                {
                    Reply(new { type = "rpcError", callId, error = new { message = "refused" } });
                }
                else
                {
                    Reply(new { type = "rpcResult", callId, result = endpoint == "getLoadConfig" ? (object)config : null });
                }
            }
            else
            {
                var channelId = packet.GetProperty("channelId").GetInt32();
                var creation = packet.GetProperty("creationParameter");
                if (Failure == "load")
                {
                    Reply(new { type = "channelError", channelId });
                }
                else
                {
                    if (Failure != "verification")
                    {
                        config = creation.GetProperty("loadConfigStack").GetProperty("layers")[0].GetProperty("config");
                    }

                    Reply(new { type = "channelSend", channelId, message = new { type = "progress", progress = 0.5 } });
                    Reply(new { type = "channelSend", channelId, message = new { type = "success", info = new { identifier = creation.GetProperty("identifier").GetString() } } });
                    Reply(new { type = "channelClose", channelId });
                }
            }

            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _receiving ??= _responses.Dequeue();
            var count = Math.Min(37, Math.Min(buffer.Count, _receiving.Length - _offset));
            _receiving.AsSpan(_offset, count).CopyTo(buffer.AsSpan());
            _offset += count;
            var end = _offset == _receiving.Length;
            if (end)
            {
                _receiving = null;
                _offset = 0;
            }

            return Task.FromResult(new WebSocketReceiveResult(count, WebSocketMessageType.Text, end));
        }
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
