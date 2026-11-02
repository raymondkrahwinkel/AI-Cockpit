namespace Cockpit.Core.Plugins;

public sealed record PluginStoreSignedSource(
    string SourceKey,
    string Organization,
    Func<CancellationToken, ValueTask<string?>> GetTokenAsync);
