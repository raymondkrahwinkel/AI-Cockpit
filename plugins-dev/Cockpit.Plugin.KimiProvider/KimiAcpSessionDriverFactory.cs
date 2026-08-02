using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider;

// `IPluginSessionDriverFactory` for the Kimi ACP provider (AC-268): deserializes the profile's
// opaque config JSON into a `KimiConfig`, resolves its `KimiConfig.Command` to a
// spawnable path via `KimiExecutableLocator`, and builds a `KimiAcpSessionDriver`
// backed by the real `ProcessCliSubprocess` — mirrors
// `Cockpit.Plugin.CliAgentProvider.CodexAppServerPluginSessionDriverFactory`.
internal sealed class KimiAcpSessionDriverFactory(Func<string, string?>? managedResolver = null) : IPluginSessionDriverFactory
{
    public IPluginSessionDriver Create(string configJson)
    {
        var config = JsonSerializer.Deserialize<KimiConfig>(configJson, KimiConfig.JsonOptions)
            ?? throw new InvalidOperationException("The Kimi provider config JSON did not deserialize.");

        // A cockpit-managed install (sub [h]), if present, is preferred over PATH.
        var executablePath = KimiExecutableLocator.Resolve(config.Command, managedResolver);
        return new KimiAcpSessionDriver(() => new ProcessCliSubprocess(), config, executablePath);
    }
}
