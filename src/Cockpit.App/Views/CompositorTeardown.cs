using Avalonia;
using Avalonia.Rendering.Composition;

namespace Cockpit.App.Views;

// AC-878: shared home for SessionView's AC-758/cc85ca1e detach-commit fix, so other surfaces with the same risk
// can call it instead of copying the block. No-op on macOS when the render clock itself has stalled — see
// AC-878 for why that case needs a different fix, not covered here.
internal static class CompositorTeardown
{
    // Fire-and-forget by design: a failure to schedule a commit must never take a close path down.
    public static void Flush(Visual? root)
    {
        if (root is not null && ElementComposition.GetElementVisual(root)?.Compositor is { } compositor)
        {
            _ = compositor.RequestCommitAsync();
        }
    }
}
