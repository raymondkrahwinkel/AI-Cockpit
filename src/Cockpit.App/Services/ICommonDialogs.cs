namespace Cockpit.App.Services;

public interface ICommonDialogs
{
    /// <summary>
    /// Opens a folder picker under <paramref name="title"/>; returns the chosen path or null if cancelled.
    /// </summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>
    /// Asks over the topmost window to confirm a destructive action. Returns true only on confirmation; Cancel, close and Escape return false.
    /// </summary>
    Task<bool> ShowConfirmationDialogAsync(string title, string message, string confirmLabel = "Remove");
}
