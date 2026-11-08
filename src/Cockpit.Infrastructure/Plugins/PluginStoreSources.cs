using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Plugins.Abstractions.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Plugins;

internal sealed class PluginStoreSources : IPluginStoreSources, ISingletonService
{
    private readonly IPluginStoreConfigStore _configStore;
    private readonly CockpitConfigFileAccess _configFile;
    private readonly ILogger<PluginStoreSources> _logger;
    private readonly Lock _sync = new();
    private readonly List<Registration> _registrations = [];

    public PluginStoreSources(IPluginStoreConfigStore configStore, ILogger<PluginStoreSources> logger)
        : this(configStore, CockpitConfigPath.Default, logger)
    {
    }

    internal PluginStoreSources(IPluginStoreConfigStore configStore, string configFilePath, ILogger<PluginStoreSources>? logger = null)
    {
        _configStore = configStore;
        _configFile = new CockpitConfigFileAccess(configFilePath);
        _logger = logger ?? NullLogger<PluginStoreSources>.Instance;
    }

    public event EventHandler? SourcesChanged;

    public IDisposable Add(IPluginStoreSourceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var registration = new Registration(this, provider);
        lock (_sync)
        {
            _registrations.Add(registration);
            provider.SourcesChanged += _OnSourcesChanged;
        }

        SourcesChanged?.Invoke(this, EventArgs.Empty);
        return registration;
    }

    public async Task<IReadOnlyList<PluginStoreSourceInfo>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configured = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var result = configured.Select(store => new PluginStoreSourceInfo(store)).ToList();
        Registration[] registrations;
        lock (_sync)
        {
            registrations = [.. _registrations];
        }

        if (registrations.Length == 0)
        {
            return result;
        }

        var config = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        var hidden = new HashSet<string>(config?.HiddenPluginStoreSources ?? [], StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var descriptor in registration.Provider.Sources)
                {
                    if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.SourceKey)
                        || !Uri.TryCreate(descriptor.IndexUrl, UriKind.Absolute, out var indexUrl)
                        || (indexUrl.Scheme != Uri.UriSchemeHttp && indexUrl.Scheme != Uri.UriSchemeHttps)
                        || !seen.Add(descriptor.SourceKey))
                    {
                        continue;
                    }

                    var store = PluginStoreConfig.Remote(descriptor.IndexUrl) with
                    {
                        Signed = new PluginStoreSignedSource(descriptor.SourceKey, descriptor.Organization, descriptor.CreateHandler),
                    };
                    result.Add(new PluginStoreSourceInfo(store, descriptor.Name, hidden.Contains(descriptor.SourceKey)));
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning("A plugin store source could not be read ({FailureType}).", exception.GetType().Name);
            }
        }

        return result;
    }

    public async Task SetHiddenAsync(string sourceKey, bool hidden, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        await _configFile.UpdateAsync(config =>
        {
            config.HiddenPluginStoreSources ??= [];
            config.HiddenPluginStoreSources.RemoveAll(key => string.Equals(key, sourceKey, StringComparison.Ordinal));
            if (hidden)
            {
                config.HiddenPluginStoreSources.Add(sourceKey);
            }
        }, cancellationToken).ConfigureAwait(false);
        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void _OnSourcesChanged(object? sender, EventArgs args) => SourcesChanged?.Invoke(this, EventArgs.Empty);

    private void _Remove(Registration registration)
    {
        lock (_sync)
        {
            _registrations.Remove(registration);
            registration.Provider.SourcesChanged -= _OnSourcesChanged;
        }

        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Registration(PluginStoreSources owner, IPluginStoreSourceProvider provider) : IDisposable
    {
        private PluginStoreSources? _owner = owner;

        public IPluginStoreSourceProvider Provider { get; } = provider;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?._Remove(this);
    }
}
