namespace Cockpit.Core.Rendering;

/// <summary>The rendering-related settings the operator controls (AC-67). Currently just the render backend.</summary>
public sealed record RenderingSettings
{
    public RenderBackendChoice Backend { get; init; } = RenderBackendChoice.Auto;
}
