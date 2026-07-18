using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Plugins;

/// <summary>
/// Holds the session-image sinks plugins register (<c>ICockpitHost.AddSessionImageSink</c>, AC-14) and delivers an
/// image-bearing user message to each. Provider-agnostic: the host routes the images, and each plugin decides what
/// to do with them (a tracker attaches them to the issue its session tracks).
/// </summary>
public interface ISessionImageSinkRegistry
{
    void Register(SessionImageSinkRegistration sink);

    /// <summary>Delivers the images to every registered sink, fail-soft: a sink that throws is logged and skipped, never breaking the send or the other sinks.</summary>
    Task NotifyAsync(SessionImageDispatch dispatch);
}

internal sealed class SessionImageSinkRegistry(ILogger<SessionImageSinkRegistry> logger)
    : ISessionImageSinkRegistry, ISingletonService
{
    private readonly List<SessionImageSinkRegistration> _sinks = [];

    public void Register(SessionImageSinkRegistration sink) => _sinks.Add(sink);

    public async Task NotifyAsync(SessionImageDispatch dispatch)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.OnImagesSent(dispatch);
            }
            catch (Exception exception)
            {
                // An attach that fails must never break the message the operator actually sent.
                logger.LogWarning(exception, "A session-image sink failed; skipping it.");
            }
        }
    }
}
