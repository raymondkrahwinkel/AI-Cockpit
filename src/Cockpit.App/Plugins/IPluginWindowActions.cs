namespace Cockpit.App.Plugins;

/// <summary>
/// What a plugin's UI part asks of this machine's window: the clipboard and the confirmation dialog.
/// </summary>
public interface IPluginWindowActions
{
    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard.
    /// </summary>
    Task SetClipboardTextAsync(string text);

    /// <summary>
    /// Asks the operator to confirm; true only when they did.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm");
}
