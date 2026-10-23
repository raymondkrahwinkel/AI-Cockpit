namespace Cockpit.Plugins.Abstractions.Notifications;

/// <summary>
/// One button on a plugin's toast (AC-1549), such as a reminder's Done or Snooze. The toast closes after a click.
/// </summary>
public sealed record PluginToastAction(string Label, Action OnInvoke);
