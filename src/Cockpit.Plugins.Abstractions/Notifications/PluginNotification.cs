namespace Cockpit.Plugins.Abstractions.Notifications;

/// <summary>
/// A notification a plugin posts to the cockpit's notification center (AC-1804). While it has
/// <see cref="Actions"/> nobody has answered, the cockpit keeps it under <see cref="Key"/> across a restart and asks
/// the plugin's notification source whether it still stands.
/// </summary>
/// <param name="Key">The plugin's own id for this notification; a later one with the same key replaces it.</param>
/// <param name="Message">The text, as on a toast.</param>
/// <param name="Severity">The toast's colour and glyph.</param>
/// <param name="Actions">Its buttons, the first one primary. None: it announces and is not kept.</param>
public sealed record PluginNotification(string Key, string Message, PluginToastSeverity Severity, IReadOnlyList<PluginToastAction> Actions)
{
    /// <summary>
    /// An optional bold line above <see cref="Message"/> (AC-1853); null shows the message alone. A host older than
    /// 0.132.0 ignores it and shows only the message.
    /// </summary>
    public string? Title { get; init; }
}
