using Cockpit.Core.Consent;

namespace Cockpit.Core.Tests.Consent;

/// <summary>
/// AC-888: a plugin's own choice of label must only be able to add rows inside its own key space, never reach
/// into the host's or another plugin's.
/// </summary>
public sealed class ConsentSourceCatalogTests
{
    [Fact]
    public void KeyFor_APluginCannotReachAnotherKeySpace()
    {
        // The id half is a folder name (PluginDiscovery: Path.GetFileName) and cannot contain a '/', so the
        // first slash after "plugin:" is always the boundary — a self-chosen label only subdivides the plugin's
        // own space.
        Assert.StartsWith("plugin:diagram/", ConsentSourceCatalog.KeyFor("diagram", "whatever"));
        Assert.NotEqual(
            ConsentSourceCatalog.KeyFor("diagram", "Diagram MCP"),
            ConsentSourceCatalog.KeyFor("docker", "../diagram/Diagram MCP"));

        // A host source never carries the prefix, so no plugin-built key can collide with one.
        Assert.DoesNotContain(ConsentSourceCatalog.HostSources, s => s.StartsWith("plugin:", StringComparison.Ordinal));
    }

    [Fact]
    public void KeyFor_TwoSurfacesOfTheSamePlugin_GetSeparateKeys()
    {
        Assert.NotEqual(
            ConsentSourceCatalog.KeyFor("diagram", "Diagram MCP"),
            ConsentSourceCatalog.KeyFor("diagram", "Whiteboard MCP"));
    }
}
