using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.PromptLibrary;

/// <summary>
/// Prompt Library (#2): a left-menu button opening a dialog of reusable prompt templates. Selecting one fills
/// in its <c>{{variable}}</c> fields and inserts the result into the active session (or copies it to the
/// clipboard when no session is active). Templates are created/edited/removed in the same dialog and persisted
/// in the host's per-plugin storage, so <see cref="ConfigureServices"/> is empty.
/// </summary>
public sealed class PromptLibraryPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "prompt-library",
        DisplayName: "Prompt Library",
        Version: "1.0.0",
        Author: "Cockpit",
        Description: "Reusable prompt templates in the left menu — click one to insert it into the active session, filling in any {{variable}} fields first.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new PromptLibrarySettings(host.Storage);
        host.AddSideMenuButton(
            "Prompt Library",
            () => _ = host.ShowDialogAsync("Prompt Library", () => new PromptLibraryDialogControl(settings, host.Actions), 900, 620));
    }

    public void Dispose()
    {
    }
}
