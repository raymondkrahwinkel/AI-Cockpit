using SkiaSharp;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// A Mac that is whatever a test says it is, and records what was asked of it. Each display captures as its own
/// colour with a white band across its top quarter — the one thing about macOS that is provable from a machine
/// that is not one is where each display's pixels ended up, and a band proves it in a way a flat fill cannot.
/// </summary>
/// <remarks>
/// The band is the point. Fill a display with one colour and a single sampled pixel reads the same whatever the
/// draw did to the source: stretched, squashed, or drawn at the wrong size entirely, as long as the corner is
/// right. Where the band's edge lands says what happened to the height.
/// </remarks>
internal sealed class StubMacScreens : IMacScreenReader
{
    private readonly Dictionary<int, SKColor> _colours = [];

    public required IReadOnlyList<MacDisplay> Displays { get; init; }

    /// <summary>Displays that write no file — screencapture without Screen Recording permission.</summary>
    public IReadOnlyList<int> CapturesNothing { get; init; } = [];

    /// <summary>Displays whose capture comes back as some other display's — what a <c>-D</c> numbering that does not line up with the enumeration would produce.</summary>
    public IReadOnlyList<int> CapturesSomeOtherDisplay { get; init; } = [];

    /// <summary>Which display indexes were asked for, in order.</summary>
    public List<int> Captured { get; } = [];

    /// <summary>The colour a given display's capture is filled with, assigned on first use so a test can look it up.</summary>
    public SKColor ColourOf(int displayIndex) => _colours[displayIndex];

    /// <summary>What the display list becomes once a capture has been taken — a screen unplugged partway through.</summary>
    public IReadOnlyList<MacDisplay>? DisplaysAfterCapture { get; init; }

    public IReadOnlyList<MacDisplay> ReadDisplays() =>
        Captured.Count > 0 && DisplaysAfterCapture is { } changed ? changed : Displays;

    public Task<byte[]?> CaptureDisplayAsync(int displayIndex, CancellationToken cancellationToken = default)
    {
        Captured.Add(displayIndex);
        if (CapturesNothing.Contains(displayIndex))
        {
            return Task.FromResult<byte[]?>(null);
        }

        var asked = Displays.First(candidate => candidate.Index == displayIndex);
        var written = CapturesSomeOtherDisplay.Contains(displayIndex)
            ? Displays.First(candidate => candidate.Index != displayIndex)
            : asked;
        var colour = new SKColor((byte)(40 * displayIndex), (byte)(90 + (20 * displayIndex)), 200);
        _colours[displayIndex] = colour;

        return Task.FromResult<byte[]?>(_Draw(written.PixelWidth, written.PixelHeight, colour));
    }

    private static byte[] _Draw(int width, int height, SKColor colour)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(colour);
        canvas.DrawRect(SKRect.Create(0, 0, width, height / 4f), new SKPaint { Color = SKColors.White });

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        return encoded.ToArray();
    }
}
