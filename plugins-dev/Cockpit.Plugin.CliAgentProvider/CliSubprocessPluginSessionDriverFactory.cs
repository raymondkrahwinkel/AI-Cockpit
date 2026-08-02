using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

// `IPluginSessionDriverFactory` for the Codex CLI provider (#45 fase B1): deserializes the
// profile's opaque config JSON into a `CliAgentConfig`, resolves its `CliAgentConfig.Command`
// to a spawnable path via `CliExecutableLocator`, and builds a `CliSubprocessPluginSessionDriver`
// backed by the real `ProcessCliSubprocess`.
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
