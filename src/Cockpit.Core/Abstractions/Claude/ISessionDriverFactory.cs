using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Claude;

/// <summary>
/// Creates the <see cref="ISessionDriver"/> a profile should run under (#26): the Claude-CLI driver for a
/// Claude profile, an OpenAI-compatible driver for a local (Ollama/LM Studio) profile. The single place a
/// provider switch lives — the session view model resolves its driver here once the profile is chosen.
/// </summary>
public interface ISessionDriverFactory
{
    ISessionDriver Create(ClaudeProfile? profile);
}
