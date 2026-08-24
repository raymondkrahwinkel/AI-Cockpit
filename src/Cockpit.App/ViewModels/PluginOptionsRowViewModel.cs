using Avalonia.Controls;

namespace Cockpit.App.ViewModels;

// One row under the Options → PLUGINS group header (AC-1005). Built fresh every time Options opens
// (CockpitViewModel._RebuildPluginOptionsRows, called from BeginOptionsEdit) rather than cached across
// sessions — a fresh CreateView() per Options open is how Cancel "reverts" a plugin's settings without the
// plugin's cooperation, the same trick ShowWidgetSettingsAsync already relies on for a widget's own form.
public sealed class PluginOptionsRowViewModel(string pluginId, string displayName, Control? content, Control? rawView, string? unavailableReason, string? category = null)
{
    public string PluginId => pluginId;

    public string DisplayName => displayName;

    // Declared via ICockpitHost.AddSettings(createView, category) (AC-1030). Null for a plugin that declared
    // none — it lands in the default PLUGINS group, same as before this existed.
    public string? Category => category;

    // What the content column shows: the plugin's settings view, wrapped with its own nav rail when it
    // declares sections (PluginSettingsBodyBuilder). Null while UnavailableReason explains why there is
    // nothing to show — the plugin is disabled, incompatible, or failed to load this session.
    public Control? Content => content;

    // The bare view instance, for staging (IPluginSettingsView.TryStage) — Content may be a rail/ScrollViewer
    // wrapper around it that does not itself implement the interface.
    public Control? RawView => rawView;

    public string? UnavailableReason => unavailableReason;
}
