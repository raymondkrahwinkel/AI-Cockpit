using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions;

// Uses the ordinary lmstudio-js /llm protocol because REST cannot carry GPU placement or K/V quantization.
internal sealed class LmStudioModelOptions(WebSocket socket)
{
    private int _callId;

    internal static async Task<string> PrepareAsync(LmStudioConfig config, string? instanceId, string modelKey, CancellationToken cancellationToken, bool allowReload = true)
    {
        _ = Fields(config);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var socket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {config.ApiKey}");
        }

        var uri = new UriBuilder($"{config.BaseUrl.TrimEnd('/')}/llm");
        uri.Scheme = uri.Scheme == "https" ? "wss" : "ws";
        await socket.ConnectAsync(uri.Uri, timeout.Token).ConfigureAwait(false);
        return await new LmStudioModelOptions(socket).ReconcileAsync(config, instanceId, modelKey, timeout.Token, allowReload).ConfigureAwait(false);
    }

    internal async Task<string> ReconcileAsync(LmStudioConfig config, string? instanceId, string modelKey, CancellationToken cancellationToken, bool allowReload = true)
    {
        var requested = Fields(config);
        var identity = Guid.NewGuid().ToString("N");
        var token = Regex.Match(config.ApiKey ?? string.Empty, "^sk-lm-(?<id>[a-zA-Z0-9]{8}):(?<key>[a-zA-Z0-9]{20})$");
        await _SendAsync(new
        {
            authVersion = 1,
            clientIdentifier = token.Success ? token.Groups["id"].Value : identity,
            clientPasskey = token.Success ? token.Groups["key"].Value : identity,
        }, cancellationToken).ConfigureAwait(false);
        var auth = await _ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (!auth.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            throw new InvalidOperationException("LM Studio SDK authentication failed; load options were not applied.");
        }

        var action = "already loaded";
        if (instanceId is null || !Matches(await _ConfigAsync(instanceId, cancellationToken).ConfigureAwait(false), requested))
        {
            if (!allowReload)
            {
                throw new InvalidOperationException("LM Studio load/reload refused: another Cockpit session uses this endpoint. Stop that session before changing model context or load options.");
            }

            action = instanceId is null ? "loaded" : "reloaded";
            if (instanceId is not null)
            {
                await _RpcAsync("unloadModel", new { identifier = instanceId }, cancellationToken).ConfigureAwait(false);
            }

            var channelId = ++_callId;
            await _SendAsync(new
            {
                type = "channelCreate",
                endpoint = "loadModel",
                channelId,
                creationParameter = new
                {
                    modelKey,
                    identifier = config.Model,
                    loadConfigStack = new { layers = new[] { new { layerName = "apiOverride", config = new { fields = requested.Select(pair => new { key = pair.Key, value = pair.Value }) } } } },
                },
            }, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var packet = await _ReceiveAsync(cancellationToken).ConfigureAwait(false);
                var type = packet.GetProperty("type").GetString();
                if (type == "channelError")
                {
                    throw _Error(packet, "LM Studio refused the requested model load options");
                }

                if (type == "channelSend" && packet.GetProperty("channelId").GetInt32() == channelId)
                {
                    var message = packet.GetProperty("message");
                    if (message.GetProperty("type").GetString() == "success")
                    {
                        instanceId = message.GetProperty("info").GetProperty("identifier").GetString()!;
                        break;
                    }
                }
                else if (type is "channelClose" or "communicationWarning")
                {
                    throw new InvalidOperationException("LM Studio closed the model load before success.");
                }
            }

            if (!Matches(await _ConfigAsync(instanceId, cancellationToken).ConfigureAwait(false), requested))
            {
                throw new InvalidOperationException("LM Studio loaded the model with different options; session startup stopped.");
            }
        }

        return $"LM Studio {config.Model}: {action}; {JsonSerializer.Serialize(requested)}";
    }

    internal static Dictionary<string, object> Fields(LmStudioConfig config)
    {
        var options = config.LoadOptions ?? throw new InvalidOperationException("No LM Studio load options requested.");
        if (options.ValidationError is { } error)
        {
            throw new InvalidOperationException(error);
        }

        var fields = new Dictionary<string, object>
        {
            ["llm.load.contextLength"] = LocalModelContext.Normalize(config.ContextTokens),
            ["llm.load.llama.autoFit"] = false,
            ["llm.load.mlx.autoFit"] = false,
        };
        void Add(string key, object? value)
        {
            if (value is not null)
            {
                fields[$"llm.load.{key}"] = value;
            }
        }

        Add("llama.acceleration.offloadRatio", options.GpuOffloadRatio);
        Add("numParallelSessions", options.Parallel);
        Add("llama.flashAttention", options.FlashAttention);
        Add("llama.kCacheQuantizationType", options.KCacheQuantization is { } k ? new { @checked = true, value = k } : null);
        Add("llama.vCacheQuantizationType", options.VCacheQuantization is { } v ? new { @checked = true, value = v } : null);
        Add("offloadKVCacheToGpu", options.OffloadKvCacheToGpu);
        Add("llama.evalBatchSize", options.EvalBatchSize);
        Add("llama.physicalBatchSize", options.PhysicalBatchSize);
        Add("llama.cpuThreadPoolSize", options.CpuThreads);
        Add("speculativeDecoding.draftMtp", options.DraftMtp);
        if (options.DraftMtp == true)
        {
            Add("speculativeDecoding.draftSimple", false);
        }

        if (!string.IsNullOrWhiteSpace(options.DraftModel))
        {
            Add("speculativeDecoding.draftModel", options.DraftModel.Trim());
            Add("speculativeDecoding.draftSimple", true);
            Add("speculativeDecoding.draftMtp", false);
        }

        Add("speculativeDecoding.draftMaxTokens", options.DraftMaxTokens);
        Add("speculativeDecoding.draftMinTokens", options.DraftMinTokens);
        Add("speculativeDecoding.draftMinContinueProbability", options.DraftMinContinueProbability);
        return fields;
    }

    internal static bool Matches(JsonElement config, IReadOnlyDictionary<string, object> requested)
    {
        var actual = config.GetProperty("fields").EnumerateArray()
            .ToDictionary(field => field.GetProperty("key").GetString()!, field => field.GetProperty("value"));
        return requested.All(pair => actual.TryGetValue(pair.Key, out var value)
            ? JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(pair.Value))
            : pair.Key is "llm.load.speculativeDecoding.draftMtp" or "llm.load.speculativeDecoding.draftSimple" && pair.Value is false);
    }

    private Task<JsonElement> _ConfigAsync(string id, CancellationToken cancellationToken) =>
        _RpcAsync("getLoadConfig", new { specifier = new { type = "query", query = new { identifier = id } } }, cancellationToken);

    private async Task<JsonElement> _RpcAsync(string endpoint, object parameter, CancellationToken cancellationToken)
    {
        var callId = ++_callId;
        await _SendAsync(new { type = "rpcCall", endpoint, callId, parameter }, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var packet = await _ReceiveAsync(cancellationToken).ConfigureAwait(false);
            var type = packet.GetProperty("type").GetString();
            if (type is "rpcError" or "communicationWarning")
            {
                throw _Error(packet, $"LM Studio SDK {endpoint} failed; session startup stopped");
            }

            if (type == "rpcResult" && packet.GetProperty("callId").GetInt32() == callId)
            {
                return packet.TryGetProperty("result", out var result) ? result : default;
            }
        }
    }

    private static InvalidOperationException _Error(JsonElement packet, string message) =>
        new(packet.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var reason)
            ? $"{message}: {reason.GetString()}"
            : message);

    private async Task _SendAsync(object packet, CancellationToken cancellationToken) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(packet).AsMemory(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

    private async Task<JsonElement> _ReceiveAsync(CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > 4 * 1024 * 1024)
            {
                throw new InvalidOperationException("Invalid LM Studio SDK response.");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
