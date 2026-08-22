namespace Cockpit.Core.Sessions;

/// <summary>
/// The one place the SDK route (<c>PluginSessionDriverAdapter</c>) and TTY route
/// (<c>PluginTtySessionProviderAdapter</c>) report a session's conversation id (AC-408). Reporting and resuming
/// are kept apart: this only carries the id, it does not store or offer to resume anything.
/// </summary>
public interface ISessionConversationSink
{
    /// <summary>
    /// Reports <paramref name="paneId"/>'s current conversation id. Called again whenever it changes (a Claude
    /// <c>/clear</c> starts a new one), not on every session event — a report that repeats the same value is not
    /// a change.
    /// </summary>
    void Report(string paneId, SessionConversationId conversation);
}
