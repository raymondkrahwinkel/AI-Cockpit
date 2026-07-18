namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// A button a plugin contributes to the Sessions toolbar (AC-91) — a global, cockpit-wide quick action shown next to
/// the workspace gear, for something the operator reaches often regardless of which session is selected. The action
/// is free: open this plugin's own settings (<see cref="ICockpitHost.ShowSettingsAsync"/>), open a dialog
/// (<see cref="ICockpitHost.ShowDialogAsync"/>), or anything else.
/// </summary>
/// <param name="Title">Short label, shown as the button's tooltip and used as its accessible name.</param>
/// <param name="Icon">Optional Material.Icons kind name (e.g. "Docker", "Kubernetes", "Cog"); a generic icon is used when null or unknown.</param>
/// <param name="OnInvoke">Runs on click, on the UI thread.</param>
public sealed record ToolbarAction(string Title, string? Icon, Func<Task> OnInvoke);
