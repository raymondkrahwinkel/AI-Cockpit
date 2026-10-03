namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// The writing side of <see cref="ISessionRegistry"/>: whoever owns a pane puts its handle in and takes it out again.
/// </summary>
public interface ISessionRegistration : ISessionRegistry
{
    /// <summary>
    /// Registers <paramref name="handle"/>, replacing an earlier handle with the same pane id.
    /// </summary>
    void Register(ISessionHandle handle);

    /// <summary>
    /// Removes the pane <paramref name="paneId"/>; nothing happens when it is not registered.
    /// </summary>
    void Unregister(string paneId);

    /// <summary>
    /// Registers the assistant, reachable through <see cref="ISessionRegistry.Assistant"/> and never listed in <see cref="ISessionRegistry.All"/>.
    /// </summary>
    void RegisterAssistant(ISessionHandle handle);

    /// <summary>
    /// Removes the assistant.
    /// </summary>
    void UnregisterAssistant();
}
