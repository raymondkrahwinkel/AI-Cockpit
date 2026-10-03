using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// The usage signals a registered session provider declares (AC-1449). Local to the desktop: a remote frontend needs
/// them over the line first (AC-1388).
/// </summary>
public interface IProviderUsageSignals
{
    /// <summary>
    /// The signals the provider registered as <paramref name="providerId"/> declares; null when none is registered.
    /// </summary>
    IReadOnlyList<PluginUsageSignal>? UsageSignalsOf(string providerId);
}
