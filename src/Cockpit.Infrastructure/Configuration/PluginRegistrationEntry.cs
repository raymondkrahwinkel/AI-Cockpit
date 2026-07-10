using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of a <see cref="PluginRegistration"/> in the <c>plugins</c> section of <c>cockpit.json</c>.</summary>
internal sealed class PluginRegistrationEntry
{
    public bool Enabled { get; set; }

    public string PinnedSha256 { get; set; } = "";

    /// <summary>The plugin's own key/value storage (<see cref="Cockpit.Plugins.Abstractions.IPluginStorage"/>); values are JSON strings. Owned by the plugin, not the load decision.</summary>
    public Dictionary<string, string> Data { get; set; } = [];

    public static PluginRegistrationEntry FromDomain(PluginRegistration registration) =>
        new() { Enabled = registration.Enabled, PinnedSha256 = registration.PinnedSha256 };

    public PluginRegistration ToDomain() => new(Enabled, PinnedSha256);
}
