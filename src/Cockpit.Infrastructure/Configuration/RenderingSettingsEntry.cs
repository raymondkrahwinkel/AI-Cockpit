using Cockpit.Core.Rendering;

namespace Cockpit.Infrastructure.Configuration;

// The `rendering` section of `cockpit.json` (AC-67): the operator's render-backend choice. The enum
// round-trips as its name via the shared `System.Text.Json.Serialization.JsonStringEnumConverter`.
internal sealed class RenderingSettingsEntry
{
    public RenderBackendChoice Backend { get; set; } = RenderBackendChoice.Auto;
    public GpuPreference GpuPreference { get; set; } = GpuPreference.Automatic;

    public static RenderingSettingsEntry FromDomain(RenderingSettings settings) =>
        new() { Backend = settings.Backend, GpuPreference = settings.GpuPreference };

    public RenderingSettings ToDomain() => new()
    {
        Backend = Backend,
        GpuPreference = Enum.IsDefined(GpuPreference) ? GpuPreference : GpuPreference.Automatic,
    };
}
