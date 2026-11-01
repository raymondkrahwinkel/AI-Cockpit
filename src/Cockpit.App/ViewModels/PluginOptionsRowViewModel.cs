using Avalonia.Controls;
using Cockpit.App.Plugins;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewModels;

// A view is built when the operator selects its plugin and is new for every Options opening.
// Cancel discards that instance, so uncommitted plugin edits need no plugin-specific revert (AC-1005).
public sealed class PluginOptionsRowViewModel(string pluginId, string displayName, Func<Control>? createView, string? unavailableReason, string? category = null, ILogger? logger = null)
{
    private Control? _content;
    private Control? _rawView;
    private string? _failure;

    public string PluginId => pluginId;

    public IReadOnlyList<Cockpit.Plugins.Abstractions.Sessions.TermsNotice> TermsNotices { get; set; } = [];

    public string DisplayName => displayName;

    public string? Category => category;

    public Control? Content => _content;

    public Control? RawView => _rawView;

    public string? UnavailableReason => _failure ?? unavailableReason;

    // Searching builds every plugin's view, so one plugin that throws here would otherwise escape through the
    // search box's binding and be drawn under it as a stack trace.
    public void EnsureContent()
    {
        if (_rawView is not null || _failure is not null || createView is null)
        {
            return;
        }

        try
        {
            _rawView = createView();
            _content = PluginSettingsBodyBuilder.Build(_rawView).Content;
        }
        catch (Exception exception)
        {
            _rawView = null;
            _content = null;
            _failure = $"{displayName}'s settings could not be opened.";
            logger?.LogError(exception, "Building the settings view of plugin {PluginId} failed", pluginId);
        }
    }
}
