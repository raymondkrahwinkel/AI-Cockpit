using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider;

/// <summary>
/// <see cref="IPluginSessionDriverFactory"/> for the Kimi ACP provider (AC-268): deserializes the profile's
/// opaque config JSON into a <see cref="KimiConfig"/>, resolves its <see cref="KimiConfig.Command"/> to a
/// spawnable path via <see cref="KimiExecutableLocator"/>, and builds a <see cref="KimiAcpSessionDriver"/>
/// backed by the real <see cref="ProcessCliSubprocess"/> — mirrors
/// <c>Cockpit.Plugin.CliAgentProvider.CodexAppServerPluginSessionDriverFactory</c>.
/// </summary>
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
