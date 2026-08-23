namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A provider's conversation id, as much as the host currently knows it (AC-408) — the shared contract both the
/// SDK route (<see cref="IPluginSessionDriver.Conversation"/>) and the TTY route
/// (<see cref="PluginTtyLaunchContext.ReportConversationId"/>) report through.
/// </summary>
/// <remarks>
/// <see cref="PluginConversationIdState.Unknown"/> means "not yet", <see cref="PluginConversationIdState.Unsupported"/>
/// is an honest "this provider has none" rather than an error, and <see cref="PluginConversationIdState.Known"/>
/// carries the real id in <see cref="Value"/>.
/// </remarks>
/// <param name="State">
/// Which of the three states this record currently represents.
/// </param>
/// <param name="Value">
/// The provider's id, set only when <paramref name="State"/> is <see cref="PluginConversationIdState.Known"/>.
/// </param>
public sealed record PluginConversationId(PluginConversationIdState State, string? Value)
{
    /// <summary>
    /// Not yet known.
    /// </summary>
    public static PluginConversationId Unknown { get; } = new(PluginConversationIdState.Unknown, null);

    /// <summary>
    /// This provider has no resumable conversation id.
    /// </summary>
    public static PluginConversationId Unsupported { get; } = new(PluginConversationIdState.Unsupported, null);

    /// <summary>
    /// The provider's real conversation id.
    /// </summary>
    public static PluginConversationId Known(string value) => new(PluginConversationIdState.Known, value);
}
