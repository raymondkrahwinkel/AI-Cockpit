using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>Combines configured stores and live plugin sources without persisting a source's credentials.</summary>
public interface IPluginStoreSources
{
    event EventHandler? SourcesChanged;

    Task<IReadOnlyList<PluginStoreSourceInfo>> LoadAsync(CancellationToken cancellationToken = default);

    Task SetHiddenAsync(string sourceKey, bool hidden, CancellationToken cancellationToken = default);
}
