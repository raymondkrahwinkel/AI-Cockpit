using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

// `IPluginSessionDriverFactory` for the interactive Codex provider (#45 phase 3). Replaces
// `CliSubprocessPluginSessionDriverFactory` as the registered Codex driver — the app-server
// route supports live approvals the headless exec route cannot.
internal sealed class CodexAppServerPluginSessionDriverFactory(Func<string, string?>? managedResolver = null) : IPluginSessionDriverFactory
{
    public IPluginSessionDriver Create(string configJson)
    {
        var config = JsonSerializer.Deserialize<CliAgentConfig>(configJson, CliAgentConfig.JsonOptions)
            ?? throw new InvalidOperationException("The CLI agent provider config JSON did not deserialize.");

        // A cockpit-managed install (AC-20), if present, is preferred over PATH.
        var executablePath = CliExecutableLocator.Resolve(config.Command, managedResolver);
        return new CodexAppServerSessionDriver(() => new ProcessCliSubprocess(), config, executablePath);
    }
}
