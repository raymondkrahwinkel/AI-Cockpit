using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions;

internal interface ILocalModelContextManager
{
    Task<string?> PrepareAsync(ProviderConfig config, CancellationToken cancellationToken = default);
}

// LM Studio's OpenAI-compatible inference endpoint cannot select a context per request. Its native v1 model API
// can, so session startup reconciles the loaded instance with the profile before the first chat call. An old server
// remains usable: inability to inspect or load is reported to the session as a warning, never as a startup failure.
internal sealed class LocalModelContextManager(HttpClient httpClient, ILogger<LocalModelContextManager> logger)
    : ILocalModelContextManager, ISingletonService
{
    public async Task<string?> PrepareAsync(ProviderConfig config, CancellationToken cancellationToken = default)
    {
        if (config is OllamaConfig ollama)
        {
            var ollamaRequested = LocalModelContext.Normalize(ollama.ContextTokens);
            logger.LogWarning(
                "Ollama profile {Model} requests {RequestedContext} context tokens, but the OpenAI-compatible endpoint cannot set num_ctx per request.",
                ollama.Model,
                ollamaRequested);
            return $"Het Ollama-profiel vraagt {ollamaRequested} contexttokens, maar de OpenAI-compatibele /v1-route kan dit niet per aanvraag instellen. Stel OLLAMA_CONTEXT_LENGTH in op de Ollama-server of gebruik een model met PARAMETER num_ctx.";
        }

        if (config is not LmStudioConfig lmStudio)
        {
            return null;
        }

        var requested = LocalModelContext.Normalize(lmStudio.ContextTokens);
        try
        {
            using var response = await _SendAsync(lmStudio, HttpMethod.Get, "/api/v1/models", null, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return _UnavailableWarning(lmStudio, requested, $"HTTP {(int)response.StatusCode}");
            }

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
            var instance = _LoadedInstance(document.RootElement, lmStudio.Model);
            if (instance is null)
            {
                return await _LoadAsync(lmStudio, requested, cancellationToken).ConfigureAwait(false);
            }

            if (instance.Value.ContextLength >= requested)
            {
                return null;
            }

            if (!lmStudio.LoadModelWithContext)
            {
                var warning = $"LM Studio is geladen met {instance.Value.ContextLength} tokens; het profiel vraagt {requested} tokens.";
                logger.LogWarning(
                    "LM Studio model {Model} is loaded with {LoadedContext} tokens while the profile requests {RequestedContext} tokens.",
                    lmStudio.Model,
                    instance.Value.ContextLength,
                    requested);
                return warning;
            }

            logger.LogInformation(
                "Reloading LM Studio model {Model}: instance {InstanceId} has {LoadedContext} context tokens; the profile requests {RequestedContext}.",
                lmStudio.Model,
                instance.Value.Id,
                instance.Value.ContextLength,
                requested);
            if (!await _PostAsync(lmStudio, "/api/v1/models/unload", new { instance_id = instance.Value.Id }, cancellationToken).ConfigureAwait(false))
            {
                return $"LM Studio-model {lmStudio.Model} kon niet worden herladen naar {requested} tokens: unload is geweigerd.";
            }

            return await _LoadAsync(lmStudio, requested, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or UriFormatException)
        {
            return _UnavailableWarning(lmStudio, requested, exception.Message, exception);
        }
    }

    private async Task<string?> _LoadAsync(LmStudioConfig config, int contextTokens, CancellationToken cancellationToken)
    {
        logger.LogInformation("Loading LM Studio model {Model} with {ContextTokens} context tokens.", config.Model, contextTokens);
        return await _PostAsync(config, "/api/v1/models/load", new { model = config.Model, context_length = contextTokens }, cancellationToken).ConfigureAwait(false)
            ? null
            : $"LM Studio-model {config.Model} kon niet met {contextTokens} tokens worden geladen; de sessie probeert de bestaande serverconfiguratie.";
    }

    private async Task<bool> _PostAsync(LmStudioConfig config, string path, object body, CancellationToken cancellationToken)
    {
        using var response = await _SendAsync(config, HttpMethod.Post, path, JsonContent.Create(body), cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        logger.LogWarning(
            "LM Studio model-context request {Path} for {Model} failed with HTTP {StatusCode}.",
            path,
            config.Model,
            (int)response.StatusCode);
        return false;
    }

    private async Task<HttpResponseMessage> _SendAsync(
        LmStudioConfig config,
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{config.BaseUrl.TrimEnd('/')}{path}") { Content = content };
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private string _UnavailableWarning(LmStudioConfig config, int requested, string reason, Exception? exception = null)
    {
        logger.LogWarning(
            exception,
            "LM Studio v1 model API is unavailable for {Model}; context {RequestedContext} could not be verified ({Reason}).",
            config.Model,
            requested,
            reason);
        return $"De LM Studio v1 model-API is niet beschikbaar; de geladen context kon niet worden gecontroleerd (profiel vraagt {requested} tokens).";
    }

    private static LoadedInstance? _LoadedInstance(JsonElement root, string model)
    {
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("LM Studio /api/v1/models returned no models array.");
        }

        foreach (var entry in models.EnumerateArray())
        {
            var keyMatches = entry.TryGetProperty("key", out var key)
                && string.Equals(key.GetString(), model, StringComparison.OrdinalIgnoreCase);
            if (!entry.TryGetProperty("loaded_instances", out var instances) || instances.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            LoadedInstance? keyCandidate = null;
            foreach (var loaded in instances.EnumerateArray())
            {
                if (!loaded.TryGetProperty("id", out var id)
                    || !loaded.TryGetProperty("config", out var loadedConfig)
                    || !loadedConfig.TryGetProperty("context_length", out var contextLength)
                    || !contextLength.TryGetInt32(out var tokens))
                {
                    continue;
                }

                var candidate = new LoadedInstance(id.GetString() ?? model, tokens);
                if (string.Equals(candidate.Id, model, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }

                if (keyMatches && (keyCandidate is null || candidate.ContextLength > keyCandidate.Value.ContextLength))
                {
                    keyCandidate = candidate;
                }
            }

            if (keyMatches)
            {
                return keyCandidate;
            }
        }

        return null;
    }

    private readonly record struct LoadedInstance(string Id, int ContextLength);
}
