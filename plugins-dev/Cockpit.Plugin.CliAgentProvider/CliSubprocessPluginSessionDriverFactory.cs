using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// <see cref="IPluginSessionDriverFactory"/> for the Codex CLI provider (#45 fase B1): deserializes the
/// profile's opaque config JSON into a <see cref="CliAgentConfig"/>, resolves its <see cref="CliAgentConfig.Command"/>
/// to a spawnable path via <see cref="CliExecutableLocator"/>, and builds a <see cref="CliSubprocessPluginSessionDriver"/>
/// backed by the real <see cref="ProcessCliSubprocess"/>.
/// </summary>
internal sealed class CliSubprocessPluginSessionDriverFactory(Func<string, string?>? managedResolver = null) : IPluginSessionDriverFactory
{
    public IPluginSessionDriver Create(string configJson)
    {
        var config = JsonSerializer.Deserialize<CliAgentConfig>(configJson, CliAgentConfig.JsonOptions)
            ?? throw new InvalidOperationException("The CLI agent provider config JSON did not deserialize.");

        // A cockpit-managed install (AC-20), if present, is preferred over PATH.
        var executablePath = CliExecutableLocator.Resolve(config.Command, managedResolver);
        return new CliSubprocessPluginSessionDriver(() => new ProcessCliSubprocess(), config, executablePath);
    }
}
