using System.Reflection;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Static content for the About dialog (#46): app identity, the running build's version, a short
/// description, which providers a session can run under, the licence, and the links to the public
/// GitHub repo, the issue tracker and the plugin store.
/// </summary>
public sealed record AboutInfo(
    string AppName,
    string VersionText,
    string Description,
    string Providers,
    string LicenseText,
    string GitHubUrl,
    string IssuesUrl,
    string PluginStoreUrl)
{
    public const string DefaultGitHubUrl = "https://github.com/raymondkrahwinkel/AI-Cockpit";
    public const string DefaultIssuesUrl = "https://github.com/raymondkrahwinkel/AI-Cockpit/issues";
    public const string DefaultPluginStoreUrl = "https://github.com/raymondkrahwinkel/AI-Cockpit-Plugins";

    /// <summary>
    /// Builds the About info from <paramref name="assembly"/>'s version metadata, preferring the
    /// informational version (carries a semver/build suffix when set) over the plain assembly version.
    /// </summary>
    public static AboutInfo FromAssembly(Assembly assembly) => new(
        "AI-Cockpit",
        _VersionText(assembly),
        "Run several AI coding sessions side by side — each in its own profile, with its own provider, permissions and transcript.",
        "Claude Code · Ollama · LM Studio · plus any provider a plugin adds (Gemini, OpenAI, GitHub Models, Codex CLI).",
        "Apache 2.0 with the Commons Clause · © 2026 Raymond Krahwinkel / Krahwinkel-IT",
        DefaultGitHubUrl,
        DefaultIssuesUrl,
        DefaultPluginStoreUrl);

    private static string _VersionText(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // The SDK appends "+<full git sha>" (SourceRevisionId) to the informational version, which
            // overflows the dialog's version line. Keep the semver part, drop the build metadata.
            var buildMetadata = informational.IndexOf('+');
            return buildMetadata < 0 ? informational : informational[..buildMetadata];
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
