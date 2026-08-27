using System.Reflection;
using System.Runtime.InteropServices;
using Cockpit.Core.Configuration;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewModels;

// The provider line is built from the live plugin registry rather than a hard-coded list — naming providers the
// operator has not installed would be advertising, not information (#46).
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
    // The build's "important numbers", shown as a faint line under the version: the plugin contract the host provides
    // (the hard compatibility gate — a plugin is refused unless its `abstractionsVersion` matches), the SDK semver
    // plugin authors build against, and the .NET runtime.
    public string BuildText { get; init; } = "";

    public const string DefaultGitHubUrl = "https://github.com/raymondkrahwinkel/AI-Cockpit";
    public const string DefaultIssuesUrl = "https://github.com/raymondkrahwinkel/AI-Cockpit/issues";
    public const string DefaultPluginStoreUrl = "https://github.com/raymondkrahwinkel/AI-Cockpit-Plugins";

    // The providers the core ships with itself, always available regardless of what is installed — the local
    // OpenAI-compatible ones.
    private static readonly string[] BuiltInProviders = ["Ollama", "LM Studio"];

    // Builds the About info from `assembly`'s version metadata, preferring the informational version (carries a
    // semver/build suffix when set) over the plain assembly version.
    public static AboutInfo FromAssembly(Assembly assembly, IEnumerable<string>? pluginProviderNames = null) => new(
        CockpitProduct.DisplayName,
        _VersionText(assembly),
        "Run several AI coding sessions side by side — each in its own profile, with its own provider, permissions and transcript.",
        _ProviderText(pluginProviderNames),
        "Apache 2.0 with the Commons Clause · © 2026 Raymond Krahwinkel / Krahwinkel-IT",
        DefaultGitHubUrl,
        DefaultIssuesUrl,
        DefaultPluginStoreUrl)
    {
        BuildText = _BuildText(),
    };

    // Built-ins first, then whatever provider plugins are installed — one flat list, because from where the
    // operator sits they are all just providers a session can run under.
    private static string _ProviderText(IEnumerable<string>? pluginProviderNames) =>
        string.Join(" · ", BuiltInProviders.Concat(pluginProviderNames ?? []));

    // "Plugin API 1 (SDK 1.4.0) · .NET 10.0.10".
    private static string _BuildText() =>
        $"Plugin API {AbstractionsContract.Version} (SDK {_VersionText(typeof(AbstractionsContract).Assembly)}) · {RuntimeInformation.FrameworkDescription}";

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
