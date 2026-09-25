using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.PromptLibrary;

// Prompt Library (AC-1395, pure UI per F2.1): a left-menu button opening a dialog of reusable prompt
// templates, plus a quick-insert palette. Both contributions (AddSideMenuButton, AddShortcut,
// ShowDialogAsync) need a window, so this project has no backend part at all.
public sealed class PromptLibraryUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        var settings = new PromptLibrarySettings(host.Storage);
        host.AddSideMenuButton(
            "Prompt Library",
            // One dialog per plugin: reopening while it's up should refocus it, not stack a second one.
            () => _ = host.ShowDialogAsync("Prompt Library", () => new PromptLibraryDialogControl(settings, host), "library", width: 900, height: 620));

        // Quick-insert palette (#: prompt quick-inject): a small search-and-inject dialog reached by the
        // keyboard shortcut and the command palette (no separate menu button — that duplicated "Prompt Library").
        // Its own key: reopening it while it's up should refocus it, not stack on top of the library dialog.
        void QuickInsert() =>
            _ = host.ShowDialogAsync("Insert prompt", () => new PromptQuickPickControl(settings, host), "insert", width: 540, height: 380);

        host.AddShortcut(new PluginShortcut("prompt-library.quick-insert", "Insert prompt", "Ctrl+Shift+P", QuickInsert));
    }
}
