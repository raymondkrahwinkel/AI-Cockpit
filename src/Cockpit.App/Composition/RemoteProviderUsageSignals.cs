using Cockpit.App.Services;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Composition;

internal sealed class RemoteProviderUsageSignals(RemoteBackend backend) : IProviderUsageSignals
{
    public IReadOnlyList<PluginUsageSignal>? UsageSignalsOf(string providerId)
    {
        if (backend.UsageSignalsOf(providerId) is not { } signals)
        {
            return null;
        }

        return
        [
            .. signals.Select(_FromWire).OfType<PluginUsageSignal>(),
        ];
    }

    private static PluginUsageSignal? _FromWire(RemoteProviderUsageSignal signal)
    {
        if (!Enum.TryParse<PluginUsageSignalKind>(signal.Kind, ignoreCase: true, out var kind))
        {
            return null;
        }

        return new PluginUsageSignal(signal.Key, signal.Label, kind, signal.DefaultThresholdPercent)
        {
            Description = signal.Description,
            SupportsResume = signal.SupportsResume,
            DefaultResumePrompt = signal.DefaultResumePrompt,
        };
    }
}
