using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Infrastructure.Projects;

// AC-1374: moved from Cockpit.App.Plugins — the registry itself has no Avalonia dependency, and works without any
// plugin loaded (it is then just empty), which is what puts it in Infrastructure rather than App (CLAUDE.md's
// layer rule).
internal sealed class SharedProjectSourceRegistry : ISharedProjectSourceRegistry, ISingletonService
{
    private readonly Dictionary<string, ISharedProjectSource> _sources = new(StringComparer.Ordinal);

    public IReadOnlyList<ISharedProjectSource> Sources => [.. _sources.Values];

    public event Action<ISharedProjectSource>? Registered;

    public bool Register(ISharedProjectSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Key) || _sources.ContainsKey(source.Key))
        {
            return false;
        }

        _sources.Add(source.Key, source);
        Registered?.Invoke(source);
        return true;
    }

    public void Remove(string key) => _sources.Remove(key);
}
