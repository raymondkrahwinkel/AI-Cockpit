using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1808: one door to what a profile's provider reports about its models, local or on a connect server.
public interface IProviderModelIntelligence
{
    /// <summary>
    /// What the provider behind <paramref name="profileLabel"/> reports right now, without a session; <see langword="null"/>
    /// means "unknown" — no such profile, a provider that reports nothing, or a read that failed — and never "zero".
    /// </summary>
    Task<PluginModelIntelligence?> ReadAsync(string profileLabel, CancellationToken cancellationToken = default);
}
