namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// A plugin's handler for images the operator sends with a user message (AC-14), registered through
/// <see cref="ICockpitHost.AddSessionImageSink"/>. The host invokes every registered sink whenever a message
/// carrying images is dispatched — provider-agnostic, so any plugin (a tracker attaching them to its issue, or
/// something else) can consume them. A sink that throws is isolated by the host and never breaks the send.
/// </summary>
/// <param name="OnImagesSent">Called with the session and its images each time an image-bearing message is sent.</param>
public sealed record SessionImageSinkRegistration(Func<SessionImageDispatch, Task> OnImagesSent);
