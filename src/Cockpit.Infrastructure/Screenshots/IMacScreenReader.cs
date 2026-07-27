namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// What macOS can be asked about its displays, and for their pixels (AC-328). Split from the capture the same
/// way the Windows one is, so what is made of the answers — where each display's pixels go in the composed
/// image — is assertable on a machine that is not a Mac.
/// </summary>
/// <remarks>
/// That split carries more weight here than it does on Windows: there is no Mac to run this against at all, so
/// everything below the seam ships unverified and everything above it is held to tests. The convention is the
/// codebase's own (<c>MacScreenLockMonitor</c>): keep the interop thin and put the decisions where they can be
/// checked.
/// </remarks>
internal interface IMacScreenReader
{
    /// <summary>Every display macOS currently reports, in <c>CGGetActiveDisplayList</c>'s order.</summary>
    IReadOnlyList<MacDisplay> ReadDisplays();

    /// <summary>
    /// One display captured whole, as PNG bytes, or <see langword="null"/> when nothing was written — which on
    /// macOS is not necessarily a failure: until Screen Recording is granted, <c>screencapture</c> runs happily
    /// and produces no file.
    /// </summary>
    Task<byte[]?> CaptureDisplayAsync(int displayIndex, CancellationToken cancellationToken = default);
}
