namespace Cockpit.Core.Wireframe.Model;

// Same keyword-is-the-name rule as WireframeNodeKind (AC-915). Pixel sizes live in the plugin
// (WireframeRenderer.SizeOf) — Core knows nothing about Avalonia.
public enum WireframeViewport
{
    Desktop,
    Tablet,
    Mobile,
}
