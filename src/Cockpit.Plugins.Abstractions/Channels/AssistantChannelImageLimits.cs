namespace Cockpit.Plugins.Abstractions.Channels;

/// <summary>
/// What an image handed to <see cref="IAssistantChannelGateway.SendAsync(string, string, IReadOnlyList{byte[]}, CancellationToken)"/>
/// may be (AC-1049). The host enforces every one of these itself — a plugin cannot raise them by ignoring them —
/// but they are public so a plugin can refuse a file before spending a download on it, and so the numbers exist
/// in one place rather than once per channel.
/// </summary>
public static class AssistantChannelImageLimits
{
    /// <summary>
    /// How many images one message may carry. Anything past this is dropped, and the sender is told.
    /// </summary>
    public const int MaxPerMessage = 4;

    /// <summary>
    /// The largest file, in bytes, that will be looked at — measured on the bytes themselves, never on a size the
    /// platform or the sender declared.
    /// </summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The largest image, per side, that will be decoded. Checked against the codec's own header before any
    /// pixels are decoded, which is what keeps a decompression bomb from being allocated in the first place.
    /// </summary>
    public const int MaxPixelsPerSide = 8000;

    /// <summary>
    /// Images longer than this on their long edge are scaled down to it; 1568 is Claude's own ceiling, above which
    /// the API scales the image down anyway. An upper bound rather than a promise — another provider may see less.
    /// </summary>
    public const int MaxLongEdge = 1568;
}
