using System.Net;

namespace Cockpit.Infrastructure.Sessions;

internal sealed class OllamaChatException(HttpStatusCode statusCode, string body, DateTimeOffset? retryAfter)
    : HttpRequestException($"Ollama HTTP {(int)statusCode}: {body}", null, statusCode)
{
    public DateTimeOffset? RetryAfter { get; } = retryAfter;
}
