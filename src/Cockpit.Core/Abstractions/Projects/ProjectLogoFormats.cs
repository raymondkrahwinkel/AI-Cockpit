namespace Cockpit.Core.Abstractions.Projects;

/// <summary>
/// The image formats a project logo may come in (AC-373). One list, because the store and the file picker each
/// used to keep their own and they drifted: the store has accepted SVG since it learned to rasterise one, while
/// the picker went on offering the platform's default set and hid every vector file the operator owned.
/// </summary>
public static class ProjectLogoFormats
{
    /// <summary>Lowercase, leading dot, as <see cref="System.IO.Path.GetExtension(string)"/> returns them.</summary>
    public static readonly IReadOnlyList<string> Extensions =
        [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico", ".svg"];
}
