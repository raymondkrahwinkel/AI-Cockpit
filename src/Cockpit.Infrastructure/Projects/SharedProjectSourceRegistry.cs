using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Infrastructure.Projects;

// AC-1374: moved from Cockpit.App.Plugins — the registry itself has no Avalonia dependency, and works without any
// plugin loaded (it is then just empty), which is what puts it in Infrastructure rather than App (CLAUDE.md's
// layer rule).
internal sealed class SharedProjectSourceRegistry : ISharedProjectSourceRegistry, ISingletonService
{
    private readonly Dictionary<string, ISharedProjectSource> _sources = new(StringComparer.Ordinal);

    // AC-1435: read off the UI thread by the project catalog and the assistant, while plugin screens register on it.
    public IReadOnlyList<ISharedProjectSource> Sources
    {
        get
        {
            lock (_sources)
            {
                return [.. _sources.Values];
            }
        }
    }

    public event Action<ISharedProjectSource>? Registered;

    public bool Register(ISharedProjectSource source)
    {
        lock (_sources)
        {
            if (string.IsNullOrWhiteSpace(source.Key) || !_sources.TryAdd(source.Key, source))
            {
                return false;
            }
        }

        Registered?.Invoke(source);
        return true;
    }

    public void Remove(string key)
    {
        lock (_sources)
        {
            _sources.Remove(key);
        }
    }
}
