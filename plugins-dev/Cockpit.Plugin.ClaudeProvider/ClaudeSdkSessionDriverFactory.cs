using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// <see cref="IPluginSessionDriverFactory"/> for the Claude SDK route (Fase 4): deserializes the profile's opaque config
/// JSON into a <see cref="ClaudeProviderConfig"/>, resolves its executable (a pinned path, else bare <c>claude</c> off
/// PATH — the bundled-executable locator is a later minor increment), and builds a <see cref="ClaudeSdkSessionDriver"/>
/// backed by the real <see cref="ProcessClaudeSdkSubprocess"/>.
/// </summary>
internal sealed class ClaudeSdkSessionDriverFactory : IPluginSessionDriverFactory
{
    public IPluginSessionDriver Create(string configJson)
    {
        var config = ClaudeProviderConfig.Parse(configJson);
        var executablePath = config.ExecutablePath is { Length: > 0 } pinned ? pinned : "claude";
        return new ClaudeSdkSessionDriver(() => new ProcessClaudeSdkSubprocess(), config, executablePath);
    }
}
