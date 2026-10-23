using Cockpit.Core.Projects;
using Avalonia.Media.Imaging;
using Cockpit.Shared;

namespace Cockpit.App.ViewModels;

// Carries its own error rather than throwing one source's failure into every other group: a connection that is not
// signed in shows its own row under its own heading, the rest of the workspace unaffected (AC-245).
public sealed record SharedProjectGroupViewModel(
    string SourceName, IReadOnlyList<SharedProjectOffer> Projects, string? Error, byte[]? SourceLogo = null)
{
    public Bitmap? Logo { get; } = _Decode(SourceLogo);

    public bool HasLogo => Logo is not null;

    public bool HasError => Error is { Length: > 0 };

    public bool HasProjects => Projects.Count > 0;

    private static Bitmap? _Decode(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var decoded = SvgRasterizer.LooksLikeSvg(bytes) ? SvgRasterizer.Rasterize(bytes, 64) : bytes;
            return decoded is null ? null : new Bitmap(new MemoryStream(decoded));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
