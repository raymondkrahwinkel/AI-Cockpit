using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Health;

namespace Cockpit.Infrastructure.Plugins;

// AC-1466: the health sections plugins register (ICockpitHost.AddHealthSection), read fresh on every health request.
// Empty without plugins, which is a healthy cockpit: a plugin that is not installed is nothing a restart fixes.
internal sealed partial class PluginHealthSections(ILogger<PluginHealthSections> logger) : ISingletonService
{
    private readonly List<IPluginHealthSection> _sections = [];

    // The name is the one plugin string an anonymous caller reads, so it is held to a slug: no path or host fits.
    // A name already taken is refused, first one wins.
    public bool Add(IPluginHealthSection section)
    {
        if (!_Slug().IsMatch(section.Name))
        {
            return false;
        }

        lock (_sections)
        {
            if (_sections.Any(existing => existing.Name == section.Name))
            {
                return false;
            }

            _sections.Add(section);
            return true;
        }
    }

    // A section that throws reads as unhealthy: a plugin that cannot say how it is doing is not doing well.
    public IReadOnlyList<(string Name, PluginHealthReport Report)> Read()
    {
        IPluginHealthSection[] sections;
        lock (_sections)
        {
            sections = [.. _sections];
        }

        return [.. sections.Select(section => (section.Name, _Read(section)))];
    }

    private PluginHealthReport _Read(IPluginHealthSection section)
    {
        try
        {
            return section.Read();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Health section '{HealthSection}' failed to report; it reads as unhealthy", section.Name);
            return new PluginHealthReport(false, []);
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex _Slug();
}
