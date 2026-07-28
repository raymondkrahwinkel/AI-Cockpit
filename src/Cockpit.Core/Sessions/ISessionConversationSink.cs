namespace Cockpit.Core.Sessions;

/// <summary>
/// The one place both the SDK route (<c>PluginSessionDriverAdapter</c>) and the TTY route
/// (<c>PluginTtySessionProviderAdapter</c>) report a session's conversation id (AC-408), so the host learns it
/// through a single seam regardless of which route the session runs on. Reporting and resuming are deliberately
/// kept apart: this ticket only carries the id to the host, it does not store or offer to resume anything.
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
