namespace Cockpit.Core.Rendering;

// The rendering-related settings the operator controls (AC-67). Currently just the render backend.
public sealed record RenderingSettings
{
    public RenderBackendChoice Backend { get; init; } = RenderBackendChoice.Auto;
}
