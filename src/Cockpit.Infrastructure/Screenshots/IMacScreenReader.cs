namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// What macOS can be asked about its displays, and for their pixels (AC-328). Split from capture, so composing
/// the answers is assertable on a machine that is not a Mac — no Mac exists to run this against, so everything
/// below the seam ships unverified (the <c>MacScreenLockMonitor</c> convention: thin interop, checkable decisions).
/// </summary>
internal interface IMacScreenReader
{
    /// <summary>
    /// Every display macOS currently reports, in <c>CGGetActiveDisplayList</c>'s order.
    /// </summary>
    IReadOnlyList<MacDisplay> ReadDisplays();

    /// <summary>
    /// One display captured whole, as PNG bytes, or <see langword="null"/> when nothing was written — which on
    /// macOS is not necessarily a failure: until Screen Recording is granted, <c>screencapture</c> runs happily
    /// and produces no file.
    /// </summary>
    Task<byte[]?> CaptureDisplayAsync(int displayIndex, CancellationToken cancellationToken = default);
}
