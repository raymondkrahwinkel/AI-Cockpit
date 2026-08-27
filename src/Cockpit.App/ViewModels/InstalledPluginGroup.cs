namespace Cockpit.App.ViewModels;

// The list is the local plugins, but the heading is the store's word for them: a manifest carries no category, so the
// only place that knows a plugin is a widget is the catalogue it came from.
public sealed record InstalledPluginGroup(string Header, IReadOnlyList<PluginRowViewModel> Plugins);
