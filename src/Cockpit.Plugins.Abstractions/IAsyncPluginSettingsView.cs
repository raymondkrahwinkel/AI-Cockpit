namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// The awaitable form of <see cref="IPluginSettingsView"/>, for a view whose write is a call over its channel to
/// the plugin's backend part (AC-1891). Implement one of the two, not both: the host stages and commits this one
/// at the same moment, in the same order and with the same failure handling, and awaits the write it hands back.
/// </summary>
public interface IAsyncPluginSettingsView
{
    /// <summary>
    /// Validates the current field values <em>without writing anything</em>, and hands the host the write to
    /// await on success. See <see cref="IPluginSettingsView.TryStage"/> for the contract of each argument.
    /// </summary>
    bool TryStage(out Func<Task>? commit, out string? error);
}
