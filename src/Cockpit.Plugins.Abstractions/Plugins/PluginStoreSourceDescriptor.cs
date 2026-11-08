namespace Cockpit.Plugins.Abstractions.Plugins;

/// <summary>A stable source identity, its location and organization, and a handler that signs in each request; the credential never leaves the host.</summary>
public sealed record PluginStoreSourceDescriptor(
    string SourceKey,
    string Name,
    string IndexUrl,
    string Organization,
    Func<HttpMessageHandler> CreateHandler);
