namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What came of <see cref="ICockpitHost.SendToSessionAsync(string, string, IReadOnlyList{byte[]})"/> (AC-2265): the same
/// shape of answer the assistant's channel gives, where the text can arrive while its images are refused.
/// </summary>
/// <param name="Delivered">
/// A live session took the message. False for an unknown pane, a pane that closed, or a message with nothing left to send.
/// </param>
/// <param name="ImagesRefused">
/// Why the images that came with the message were not passed on, plain enough to show the sender; null when there was
/// nothing to refuse. The text may still have arrived: read <paramref name="Delivered"/> for that.
/// </param>
public sealed record SessionSendResult(bool Delivered, string? ImagesRefused = null);
