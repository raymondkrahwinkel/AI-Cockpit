using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Health;

namespace Cockpit.Infrastructure.Plugins;

// AC-1466: the health sections plugins register (ICockpitHost.AddHealthSection), read fresh on every health request.
// Empty without plugins, which is a healthy cockpit: a plugin that is not installed is nothing a restart fixes.
internal sealed partial class PluginHealthSections(ILogger<PluginHealthSections> logger) : ISingletonService
{
    public const int MaxSections = 16;

    // Under Docker's own probe timeout, so a slow section reads unhealthy instead of the probe timing out.
    public static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(2);

    private readonly List<_Entry> _entries = [];

    // The name is the one plugin string an anonymous caller reads, so it is taken once here and held to a slug: no
    // path or host fits, and the getter cannot change it later. A taken name or one past the cap is refused.
    public bool Add(string pluginId, IPluginHealthSection section)
    {
        string name;
        try
        {
            name = section.Name;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Plugin {PluginId} registered a health section whose name could not be read; it is ignored", pluginId);
            return false;
        }

        lock (_entries)
        {
            var refusal = !_Slug().IsMatch(name) ? "is not a lowercase slug of at most 40 characters"
                : _entries.Any(existing => existing.Name == name) ? "is already registered"
                : _entries.Count >= MaxSections ? $"would pass the cap of {MaxSections} sections"
                : null;
            if (refusal is not null)
            {
                logger.LogWarning("Health section '{HealthSection}' of plugin {PluginId} {Refusal}; it is ignored", name, pluginId, refusal);
                return false;
            }

            _entries.Add(new _Entry(name, section));
            return true;
        }
    }

    // AC-1470: `Actions` is the section itself when it offers any; /healthz never looks at it.
    public async Task<IReadOnlyList<(string Name, PluginHealthReport Report, IPluginHealthActions? Actions)>> ReadAsync()
    {
        _Entry[] entries;
        lock (_entries)
        {
            entries = [.. _entries];
        }

        return await Task.WhenAll(entries.Select(_ReadAsync)).ConfigureAwait(false);
    }

    // A read still running from an earlier request is waited on again rather than started anew, so a section that
    // hangs holds one thread however many probes arrive. Late reads as unhealthy, like a throw.
    private async Task<(string Name, PluginHealthReport Report, IPluginHealthActions? Actions)> _ReadAsync(_Entry entry)
    {
        Task<PluginHealthReport> read;
        lock (entry)
        {
            read = entry.Pending is { IsCompleted: false } pending ? pending : entry.Pending = Task.Run(() => _Read(entry));
        }

        try
        {
            return (entry.Name, await read.WaitAsync(ReadBudget).ConfigureAwait(false), entry.Section as IPluginHealthActions);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Health section '{HealthSection}' did not report within {ReadBudget}; it reads as unhealthy", entry.Name, ReadBudget);
            return (entry.Name, new PluginHealthReport(false, []), entry.Section as IPluginHealthActions);
        }
    }

    // A section that throws reads as unhealthy: a plugin that cannot say how it is doing is not doing well.
    private PluginHealthReport _Read(_Entry entry)
    {
        try
        {
            return entry.Section.Read();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Health section '{HealthSection}' failed to report; it reads as unhealthy", entry.Name);
            return new PluginHealthReport(false, []);
        }
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,39}\z")]
    private static partial Regex _Slug();

    private sealed class _Entry(string name, IPluginHealthSection section)
    {
        public string Name { get; } = name;

        public IPluginHealthSection Section { get; } = section;

        public Task<PluginHealthReport>? Pending { get; set; }
    }
}
