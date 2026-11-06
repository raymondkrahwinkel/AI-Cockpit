namespace Cockpit.Core.HeavyRuns;

public interface IHeavyRunSettingsRefresh
{
    /// <summary>
    /// Reloads the saved limits and reconsiders queued runs after Options applies them.
    /// </summary>
    Task RefreshSettingsAsync();
}
