using System.Reflection;
using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The pending image chip decodes a full Avalonia <c>Bitmap</c> that used to be reclaimed only by the GC
/// finalizer. It must now be disposed deterministically when the chip actually leaves the UI (per-chip
/// remove, and the send-path clear) — but never while the chip is still shown, since the bitmap is bound
/// to a visible Image control.
/// </summary>
[Collection("avalonia")]
public sealed class PendingAttachmentDisposeTests
{
    // 1x1 transparent PNG — just enough for Bitmap to decode.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void RemovingAChip_DisposesItsThumbnail_ButNotWhileStillPresent() => HeadlessAvalonia.Run(() =>
    {
        var vm = new SessionViewModel();
        vm.AddPastedImage(TinyPng);
        var attachment = Assert.Single(vm.PendingAttachments);

        // Still shown → must not be disposed.
        Assert.False(attachment.IsDisposed);

        attachment.RemoveCommand.Execute(null);

        Assert.Empty(vm.PendingAttachments);
        Assert.True(attachment.IsDisposed);
    });

    [Fact]
    public void ClearingOnSend_DisposesEveryThumbnail() => HeadlessAvalonia.Run(() =>
    {
        var vm = new SessionViewModel();
        vm.AddPastedImage(TinyPng);
        vm.AddPastedImage(TinyPng);
        var attachments = vm.PendingAttachments.ToList();

        // Present in the list → none disposed yet.
        Assert.All(attachments, a => Assert.False(a.IsDisposed));

        typeof(SessionViewModel)
            .GetMethod("_ClearPendingAttachments", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, null);

        Assert.Empty(vm.PendingAttachments);
        Assert.All(attachments, a => Assert.True(a.IsDisposed));
    });
}
