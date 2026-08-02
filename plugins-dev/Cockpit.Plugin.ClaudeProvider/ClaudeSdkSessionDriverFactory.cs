using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// `IPluginSessionDriverFactory` for the Claude SDK route (Fase 4): deserializes the profile's opaque config
// JSON into a `ClaudeProviderConfig`, resolves its executable (a pinned path, else bare `claude` off
// PATH — the bundled-executable locator is a later minor increment), and builds a `ClaudeSdkSessionDriver`
// backed by the real `ProcessClaudeSdkSubprocess`.
internal sealed class ClaudeSdkSessionDriverFactory(Func<string, string?>? managedResolver = null) : IPluginSessionDriverFactory
{
    public IPluginSessionDriver Create(string configJson)
    {
        var config = ClaudeProviderConfig.Parse(configJson);
        // Resolve the command to a spawnable path — a pinned path passes through, a cockpit-managed install (AC-20) is
        // preferred next, then a bare "claude" is looked up on PATH (and, on Windows, matched against the .cmd npm
        // shim, which Process does not do itself).
        var executablePath = ClaudeExecutableLocator.Resolve(config.ExecutablePath is { Length: > 0 } pinned ? pinned : "claude", managedResolver);
        return new ClaudeSdkSessionDriver(() => new ProcessClaudeSdkSubprocess(), config, executablePath);
    }
}
