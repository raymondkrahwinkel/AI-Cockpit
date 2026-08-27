using Avalonia.Controls;
using Cockpit.App.Plugins;

namespace Cockpit.App.ViewModels;

// A view is built when the operator selects its plugin and is new for every Options opening.
// Cancel discards that instance, so uncommitted plugin edits need no plugin-specific revert (AC-1005).
public sealed class PluginOptionsRowViewModel(string pluginId, string displayName, Func<Control>? createView, string? unavailableReason, string? category = null)
{
    private Control? _content;
    private Control? _rawView;

    public string PluginId => pluginId;

    public string DisplayName => displayName;

    public string? Category => category;

    public Control? Content => _content;

    public Control? RawView => _rawView;

    public string? UnavailableReason => unavailableReason;

    public void EnsureContent()
    {
        if (_rawView is not null || createView is null)
        {
            return;
        }

        _rawView = createView();
        _content = PluginSettingsBodyBuilder.Build(_rawView).Content;
    }
}
