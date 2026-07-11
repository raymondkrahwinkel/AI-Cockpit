namespace Cockpit.App.Services;

/// <summary>
/// Checks the configured plugin stores (#14) for a newer version of an installed plugin and toasts once
/// per newly detected update (#59). Fails silently on any network/parse problem — a store being
/// unreachable must never crash the app or interrupt the timer loop driving <see cref="CheckNowAsync"/>.
/// </summary>
public interface IPluginUpdateChecker
{
    /// <summary>Runs one check pass. Called once at startup (after plugin phase-2) and every 15 minutes by <see cref="App"/>.</summary>
    Task CheckNowAsync(CancellationToken cancellationToken = default);
}
