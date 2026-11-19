namespace Cockpit.Core.Rendering;

public sealed record RenderingSettings
{
    public RenderBackendChoice Backend { get; init; } = RenderBackendChoice.Auto;
    public string GpuDeviceName { get; init; } = string.Empty;
}
