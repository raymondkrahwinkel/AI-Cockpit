namespace Cockpit.Core.Abstractions.Claude;

/// <summary>
/// Lists the models a local OpenAI-compatible server (Ollama/LM Studio) currently has available, via its
/// <c>/v1/models</c> endpoint (#26), so a profile can pick from installed models instead of typing an id.
/// </summary>
public interface IModelCatalog
{
    /// <summary>
    /// Returns the model ids reported by <paramref name="baseUrl"/>'s <c>/v1/models</c>, or an empty list
    /// when the server is unreachable or returns nothing — implementations never throw to the caller, so a
    /// stopped local server just yields no suggestions.
    /// </summary>
    Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string? apiKey = null, CancellationToken cancellationToken = default);
}
