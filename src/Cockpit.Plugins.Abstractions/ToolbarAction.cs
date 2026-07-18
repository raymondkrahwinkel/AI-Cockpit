using Material.Icons;

namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// A button a plugin contributes to the Sessions toolbar (AC-91) — a global, cockpit-wide quick action shown next to
/// the workspace gear, for something the operator reaches often regardless of which session is selected. The action
/// is free: open this plugin's own settings (<see cref="ICockpitHost.ShowSettingsAsync"/>), open a dialog
/// (<see cref="ICockpitHost.ShowDialogAsync"/>), or anything else.
/// </summary>
/// <param name="Title">Short label, shown as the button's tooltip and used as its accessible name.</param>
/// <param name="Icon">Bundled vector icon to show (e.g. <see cref="MaterialIconKind.Docker"/>); a generic icon is used when null.</param>
/// <param name="OnInvoke">Runs on click, on the UI thread.</param>
public sealed record ToolbarAction(string Title, MaterialIconKind? Icon, Func<Task> OnInvoke);
