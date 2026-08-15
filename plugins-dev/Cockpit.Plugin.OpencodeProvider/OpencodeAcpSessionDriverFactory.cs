using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider;

// AC-783: deserializes the config JSON, resolves the command to a spawnable path, and builds a driver
// backed by the real ProcessCliSubprocess — mirrors KimiAcpSessionDriverFactory.
internal sealed class OpencodeAcpSessionDriverFactory(Func<string, string?>? managedResolver = null) : IPluginSessionDriverFactory
{
    public IPluginSessionDriver Create(string configJson)
    {
        var config = JsonSerializer.Deserialize<OpencodeConfig>(configJson, OpencodeConfig.JsonOptions)
            ?? throw new InvalidOperationException("The opencode provider config JSON did not deserialize.");

        // A cockpit-managed install, if present, is preferred over PATH.
        var executablePath = OpencodeExecutableLocator.Resolve(config.Command, managedResolver);
        return new OpencodeAcpSessionDriver(() => new ProcessCliSubprocess(), config, executablePath);
    }
}
