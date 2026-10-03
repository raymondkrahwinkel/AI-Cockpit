namespace Cockpit.Core.Abstractions;

/// <summary>
/// Folds the settings writes one async flow makes into a single write (AC-1108).
/// </summary>
public interface ISettingsWriteBatch
{
    /// <summary>
    /// Opens a batch for the current async flow; disposing it writes what the flow collected.
    /// </summary>
    IAsyncDisposable Begin();
}
