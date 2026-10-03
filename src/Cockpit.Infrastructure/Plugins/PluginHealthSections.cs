using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Health;

namespace Cockpit.Infrastructure.Plugins;

// AC-1466: the health sections plugins register (ICockpitHost.AddHealthSection), read fresh on every health request.
// Empty without plugins, which is a healthy cockpit: a plugin that is not installed is nothing a restart fixes.
internal sealed class PluginHealthSections(ILogger<PluginHealthSections> logger) : ISingletonService
{
    private readonly List<IPluginHealthSection> _sections = [];

    public void Add(IPluginHealthSection section)
    {
        lock (_sections)
        {
            _sections.Add(section);
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
}
