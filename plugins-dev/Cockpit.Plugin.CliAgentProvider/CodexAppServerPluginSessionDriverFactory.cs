using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// <see cref="IPluginSessionDriverFactory"/> for the interactive Codex provider (#45 fase 3): deserializes the
/// profile's opaque config JSON into a <see cref="CliAgentConfig"/>, resolves its <see cref="CliAgentConfig.Command"/>
/// to a spawnable path via <see cref="CliExecutableLocator"/>, and builds a <see cref="CodexAppServerSessionDriver"/>
/// backed by the real <see cref="ProcessCliSubprocess"/>. Replaces <see cref="CliSubprocessPluginSessionDriverFactory"/>
/// as the registered Codex driver — the app-server route supports live approvals the headless exec route cannot.
/// </summary>
internal sealed class CodexAppServerPluginSessionDriverFactory : IPluginSessionDriverFactory
{
    public IPluginSessionDriver Create(string configJson)
    {
        var config = JsonSerializer.Deserialize<CliAgentConfig>(configJson, CliAgentConfig.JsonOptions)
            ?? throw new InvalidOperationException("The CLI agent provider config JSON did not deserialize.");

        var executablePath = CliExecutableLocator.Resolve(config.Command);
        return new CodexAppServerSessionDriver(() => new ProcessCliSubprocess(), config, executablePath);
    }
}
