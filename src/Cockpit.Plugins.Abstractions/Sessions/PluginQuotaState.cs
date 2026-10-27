namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// How far one usage window stood when the provider last read it (AC-1808): the same <see cref="PluginRateLimitWindow"/>
/// a session reports, with the moment and the source of the reading, so a figure from yesterday never reads as now.
/// </summary>
/// <param name="Window">
/// The window as it stood at <see cref="PluginFactSource.AsOf"/>.
/// </param>
/// <param name="Source">
/// Where the reading came from and how long it holds.
/// </param>
public sealed record PluginQuotaState(PluginRateLimitWindow Window, PluginFactSource Source);
