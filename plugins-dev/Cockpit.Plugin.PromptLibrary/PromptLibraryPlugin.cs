using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.PromptLibrary;

// Prompt Library (#2): a left-menu button opening a dialog of reusable prompt templates. Selecting one fills
// in its `{{variable}}` fields and inserts the result into the active session (or copies it to the
// clipboard when no session is active). Templates are created/edited/removed in the same dialog and persisted
// in the host's per-plugin storage, so `ConfigureServices` is empty.
public sealed class PromptLibraryPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "prompt-library",
        DisplayName: "Prompt Library",
        Author: "Cockpit",
        Description: "Reusable prompt templates in the left menu — click one to insert it into the active session, filling in any {{variable}} fields first. Plus a spotlight-style quick-insert (the Ctrl+Shift+P shortcut, or the command palette): a search bar over your prompts — type, then click or Enter to drop one into the session.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new PromptLibrarySettings(host.Storage);
        host.AddSideMenuButton(
            "Prompt Library",
            // One dialog per plugin: reopening while it's up should refocus it, not stack a second one.
            () => _ = host.ShowDialogAsync("Prompt Library", () => new PromptLibraryDialogControl(settings, host.Actions), "library", width: 900, height: 620));

        // Quick-insert palette (#: prompt quick-inject): a small search-and-inject dialog reached by the
        // keyboard shortcut and the command palette (no separate menu button — that duplicated "Prompt Library").
        // Its own key: reopening it while it's up should refocus it, not stack on top of the library dialog.
        void QuickInsert() =>
            _ = host.ShowDialogAsync("Insert prompt", () => new PromptQuickPickControl(settings, host.Actions), "insert", width: 540, height: 380);

        host.AddShortcut(new PluginShortcut("prompt-library.quick-insert", "Insert prompt", "Ctrl+Shift+P", QuickInsert));
    }

    public void Dispose()
    {
    }
}
