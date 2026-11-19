namespace Cockpit.Core.Rendering;

public sealed record RenderingSettings
{
    public RenderBackendChoice Backend { get; init; } = RenderBackendChoice.Auto;
    public GpuPreference GpuPreference { get; init; } = GpuPreference.Automatic;
}
