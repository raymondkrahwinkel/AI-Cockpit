using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// <see cref="ILocalLlmEndpointResolver"/> that auto-detects the running local model server the same way the
/// memory breakdown does (process name, via <see cref="LocalModelServers"/>), maps it to its default port, and
/// reads its model list (through the shared <see cref="IModelCatalog"/>) to choose one. Falls back to the
/// operator's configured URL/model whenever auto-detect is off, the process table can't be read, or no detected
/// server is actually serving.
/// </summary>
internal sealed class LocalLlmEndpointResolver(IProcessTableReader processTable, IModelCatalog modelCatalog, ILogger<LocalLlmEndpointResolver> logger)
    : ILocalLlmEndpointResolver, ISingletonService
{
    // The ports the two servers listen on out of the box; a running server is mapped to its URL here. A custom
    // port is exactly what the manual fallback is for.
    private static readonly IReadOnlyDictionary<string, string> DefaultUrls = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["LM Studio"] = "http://localhost:1234",
        ["Ollama"] = "http://localhost:11434",
    };

    public async Task<LocalLlmEndpoint> ResolveAsync(VoiceSettings settings, CancellationToken cancellationToken = default)
    {
        var manual = new LocalLlmEndpoint(settings.VoiceLlmBaseUrl, settings.VoiceLlmModel);
        if (!settings.AutoDetectLocalLlm)
        {
            return manual;
        }

        // Which servers are running right now — the same process-name detection the memory breakdown uses (#78),
        // heaviest first, so a machine with a big model loaded is tried before an idle second server.
        IReadOnlyList<ModelServerUsage> running;
        try
        {
            running = LocalModelServers.From(processTable.Read());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local LLM auto-detect could not read the process table; using the configured endpoint");
            return manual;
        }

        foreach (var server in _ApplyPreference(running, settings.LocalLlmPreference))
        {
            if (!DefaultUrls.TryGetValue(server.Name, out var baseUrl))
            {
                continue;
            }

            var models = await modelCatalog.ListModelsAsync(baseUrl, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (models is not { Count: > 0 })
            {
                // The process is up but its HTTP server is not serving models (e.g. LM Studio open, server not
                // started) — try the next detected server rather than pinning to one that cannot answer.
                continue;
            }

            var model = _PickModel(models, settings.VoiceLlmModel);
            if (model is not null)
            {
                return new LocalLlmEndpoint(baseUrl, model);
            }
        }

        return manual;
    }

    /// <summary>
    /// Puts the operator's preferred server first when both are running, so a specific choice wins the tie; the
    /// rest keep their heaviest-first order, so an unavailable preference still degrades to whatever is detected.
    /// </summary>
    private static IEnumerable<ModelServerUsage> _ApplyPreference(IReadOnlyList<ModelServerUsage> running, LocalLlmPreference preference)
    {
        var preferredName = preference switch
        {
            LocalLlmPreference.Ollama => "Ollama",
            LocalLlmPreference.LmStudio => "LM Studio",
            _ => null,
        };

        return preferredName is null
            ? running
            : running.OrderByDescending(server => string.Equals(server.Name, preferredName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Prefers the configured model when the server actually has it; otherwise an instruction-tuned model (what
    /// cleanup needs — it follows the "reply with only the cleaned text" instruction), then a "mini"/"small"
    /// build, then the first chat model. Embedding models are never eligible. Size digits in an id ("30b", "a3b")
    /// are too unreliable to read — "a3b" is a 35B mixture-of-experts — so only name signals are used.
    /// </summary>
    private static string? _PickModel(IReadOnlyList<string> available, string preferred)
    {
        if (available.Contains(preferred, StringComparer.OrdinalIgnoreCase))
        {
            return preferred;
        }

        var chat = available.Where(id => id.IndexOf("embed", StringComparison.OrdinalIgnoreCase) < 0).ToList();
        if (chat.Count == 0)
        {
            return null;
        }

        static bool Has(string id, string token) => id.Contains(token, StringComparison.OrdinalIgnoreCase);
        return chat.FirstOrDefault(id => Has(id, "instruct"))
            ?? chat.FirstOrDefault(id => Has(id, "mini") || Has(id, "small"))
            ?? chat[0];
    }
}
