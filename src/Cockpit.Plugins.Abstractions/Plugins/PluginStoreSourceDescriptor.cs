namespace Cockpit.Plugins.Abstractions.Plugins;

/// <summary>A stable source identity and its current location, organization and request-time credential.</summary>
public sealed record PluginStoreSourceDescriptor(
    string SourceKey,
    string Name,
    string IndexUrl,
    string Organization,
    Func<CancellationToken, ValueTask<string?>> GetTokenAsync);
