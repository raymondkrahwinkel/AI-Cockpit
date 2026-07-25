using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Where a captured screenshot lands in a chat session (AC-220): as the same removable thumbnail chip a CTRL+V
/// paste produces, waiting for the operator to type the sentence that goes with it.
/// </summary>
/// <remarks>
/// Here rather than in the unit tests because queuing an attachment decodes a preview bitmap, and Avalonia
/// cannot decode one without a platform — the refusal branches, which decode nothing, are asserted over there.
/// </remarks>
[Collection("avalonia")]
public class ScreenshotAttachmentViewTests
{
    /// <summary>A real 1×1 PNG: the attachment chip decodes what it is given, so bytes that only look like a PNG will not do.</summary>
    private static byte[] Png => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public void AVisionSession_QueuesTheScreenshotAsAPendingAttachment() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        var reason = session.InjectScreenshot(Png);

        reason.Should().BeNull("nothing needs explaining when it landed");
        session.PendingAttachments.Should().ContainSingle();
        session.PendingAttachments[0].PngBytes.Should().Equal(Png);
    });

    /// <summary>
    /// No auto-submit: a screenshot is nearly always "look at this, because…", so it waits in the composer for
    /// the sentence that goes with it rather than being shot off on its own.
    /// </summary>
    [Fact]
    public void AVisionSession_DoesNotSendTheScreenshotOnItsOwn() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        session.InjectScreenshot(Png);

        session.InputText.Should().BeEmpty();
        session.PendingAttachments.Should().ContainSingle("it is queued for the operator to send, not sent");
    });

    /// <summary>Several in a row are several attachments — one capture must not replace the last.</summary>
    [Fact]
    public void ScreenshotsTakenInARow_EachGetTheirOwnAttachment() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        session.InjectScreenshot(Png);
        session.InjectScreenshot(Png);

        session.PendingAttachments.Should().HaveCount(2);
    });

    /// <summary>The chip is removable, so a screenshot taken by accident can be dropped before the message goes.</summary>
    [Fact]
    public void AQueuedScreenshot_CanBeRemovedBeforeSending() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.InjectScreenshot(Png);

        session.PendingAttachments[0].RemoveCommand.Execute(null);

        session.PendingAttachments.Should().BeEmpty();
    });

    /// <summary>Send is enabled by a screenshot alone: "look at this" with no words is a complete message.</summary>
    [Fact]
    public void AScreenshotAlone_IsEnoughToSend() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        session.InjectScreenshot(Png);

        session.CanSend.Should().BeTrue();
    });

    /// <summary>A provider that cannot see images attaches nothing here either — the gate is the view model's, not the button's.</summary>
    [Fact]
    public void ANonVisionSession_AttachesNothing() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel
        {
            Capabilities = SessionCapabilities.ClaudeCli with { SupportsVision = false },
        };

        var reason = session.InjectScreenshot(Png);

        reason.Should().NotBeNull();
        session.PendingAttachments.Should().BeEmpty();
    });
}
