using Cockpit.Plugin.LocalCi.Contracts;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.LocalCi.UI;

// The settings page's own read/write of this plugin's storage — the same keys the backend part's LocalCiSettings
// reads (McpEnabled gates the MCP endpoint, RunnerImage feeds every run, SkipConsent the MCP tool's consent check).
// Duplicated rather than shared: LocalCiSettings lives in the backend project, out of the UI part's reach, and
// IPluginStorage is the same slice on both sides of the split (see ICockpitUiHost.Storage).
internal sealed class LocalCiUiSettings(IPluginStorage storage)
{
    public bool McpEnabled
    {
        get => storage.Get<bool?>("mcpEnabled") ?? true;
        set => storage.Set("mcpEnabled", value);
    }

    public string RunnerImage
    {
        get => storage.Get<string>("runnerImage") is { Length: > 0 } image ? image : LocalCiChannel.DefaultRunnerImage;
        set => storage.Set("runnerImage", value?.Trim() ?? string.Empty);
    }

    // AC-710: off by default, so a fresh install still asks every time.
    public bool SkipConsent
    {
        get => storage.Get<bool?>("skipConsent") ?? false;
        set => storage.Set("skipConsent", value);
    }
}
