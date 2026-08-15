using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider;

// `IPluginSessionDriverFactory` for the opencode ACP provider (AC-783): deserializes the profile's opaque
// config JSON into an `OpencodeConfig`, resolves its `OpencodeConfig.Command` to a spawnable path via
// `OpencodeExecutableLocator`, and builds an `OpencodeAcpSessionDriver` backed by the real
// `ProcessCliSubprocess` — mirrors `Cockpit.Plugin.KimiProvider.KimiAcpSessionDriverFactory`.
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
