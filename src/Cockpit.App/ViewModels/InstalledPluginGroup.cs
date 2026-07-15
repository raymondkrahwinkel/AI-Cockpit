namespace Cockpit.App.ViewModels;

/// <summary>
/// One heading in the store dialog's <b>Installed</b> list — "Widgets", "AI providers", … — and the plugins
/// under it. The list is the local plugins, but the heading is the store's word for them: a manifest carries no
/// category, so the only place that knows a plugin is a widget is the catalogue it came from.
/// </summary>
/// <param name="Header">
/// The category, matched from the catalogue by plugin id, or <see cref="StorePluginRowViewModel.OtherCategory"/>
/// for one no configured store lists. That bucket is not a filler: a plugin nobody offers is either sideloaded
/// from a zip or left behind by a store that dropped it, and both are worth seeing as their own group rather
/// than filed under a category that no longer claims them.
/// </param>
public sealed record InstalledPluginGroup(string Header, IReadOnlyList<PluginRowViewModel> Plugins);
