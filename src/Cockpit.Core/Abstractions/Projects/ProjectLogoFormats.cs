namespace Cockpit.Core.Abstractions.Projects;

// The image formats a project logo may come in (AC-373). One list, because the store and the file picker each
// used to keep their own and they drifted: the store has accepted SVG since it learned to rasterise one, while
// the picker went on offering the platform's default set and hid every vector file the operator owned.
public static class ProjectLogoFormats
{
    // Lowercase, leading dot, as `System.IO.Path.GetExtension(string)` returns them.
    public static readonly IReadOnlyList<string> Extensions =
        [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico", ".svg"];
}
