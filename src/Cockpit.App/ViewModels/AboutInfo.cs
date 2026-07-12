using System.Reflection;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Static content for the About dialog (#46): app identity, the running build's version, a short
/// description, and the links to the public GitHub repo and plugin store.
/// </summary>
public sealed record AboutInfo(string AppName, string VersionText, string Description, string GitHubUrl, string PluginStoreUrl)
{
    public const string DefaultGitHubUrl = "https://github.com/raymondkrahwinkel/AI-Cockpit";
    public const string DefaultPluginStoreUrl = "https://github.com/raymondkrahwinkel/AI-Cockpit-Plugins";

    /// <summary>
    /// Builds the About info from <paramref name="assembly"/>'s version metadata, preferring the
    /// informational version (carries a semver/build suffix when set) over the plain assembly version.
    /// </summary>
    public static AboutInfo FromAssembly(Assembly assembly) => new(
        "AI-Cockpit",
        _VersionText(assembly),
        "Run several AI coding sessions side by side — Claude Code, local Ollama/LM Studio models, or a provider added by a plugin.",
        DefaultGitHubUrl,
        DefaultPluginStoreUrl);

    private static string _VersionText(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
