using Microsoft.Extensions.AI;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Builds a Microsoft.Extensions.AI <see cref="IChatClient"/> for a local OpenAI-compatible provider
/// (Ollama/LM Studio) from its profile config. A seam so <see cref="OpenAiCompatSessionDriver"/> can be
/// unit-tested against a fake chat client without a running server.
/// </summary>
internal interface IChatClientFactory
{
    IChatClient Create(ProviderConfig config);
}
