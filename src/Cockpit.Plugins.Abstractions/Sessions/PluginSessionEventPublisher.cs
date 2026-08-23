using System.Threading.Channels;

namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// The channel a session driver publishes its events on, with a ceiling and an honest account of what the ceiling
/// cost (AC-308). Every driver had grown its own <c>CreateUnbounded</c>, which is the one place a child process could
/// grow host memory with nothing saying so: the bounded transport underneath a driver gives a ceiling that stops at
/// the driver when the driver has none of its own.
/// </summary>
/// <remarks>
/// <para>
/// A ceiling with reported loss rather than backpressure, and that is a deliberate choice over the alternative. Real
/// backpressure would mean every publish awaiting the host, which is a different shape for all forty-odd call sites
/// across the drivers — several of them in synchronous context. This keeps their shape and mirrors what the layer
/// above already does: <c>SessionRuntime</c> trims its own event log at a cap and counts what it dropped.
/// </para>
/// <para>
/// <see cref="BoundedChannelFullMode.Wait"/> looks like the wrong mode for a type that never awaits, and it is the
/// only correct one: it is the only mode in which <see cref="ChannelWriter{T}.TryWrite"/> <em>reports</em> that it
/// could not write. Every drop mode makes <c>TryWrite</c> succeed and discard silently — which is precisely the
/// failure this type exists to prevent.
/// </para>
/// <para>
/// The capacity is a safety ceiling, not a flow-control knob: a turn streaming prose a token at a time stays far
/// below it, so an operator meets this only when something is already wrong.
/// </para>
/// </remarks>
public sealed class PluginSessionEventPublisher
{
    /// <summary>
    /// How many events may sit unread before further ones are dropped and counted.
    /// </summary>
    public const int Capacity = 4096;

    private readonly Channel<PluginSessionEvent> _channel =
        Channel.CreateBounded<PluginSessionEvent>(new BoundedChannelOptions(Capacity)
        {
            // See the remarks: Wait is the only mode under which TryWrite tells us it failed.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private int _dropped;

    /// <summary>
    /// The events, for the host's pump. Enumerated once per driver.
    /// </summary>
    public IAsyncEnumerable<PluginSessionEvent> Events => _channel.Reader.ReadAllAsync();

    /// <summary>
    /// How many events have been dropped and not yet reported to the host.
    /// </summary>
    public int PendingDroppedCount => Volatile.Read(ref _dropped);

    /// <summary>
    /// Publishes <paramref name="sessionEvent"/>, or counts it as dropped when the host is more than
    /// <see cref="Capacity"/> events behind. Returns whether it was published — callers may ignore that, because the
    /// count is what carries the loss: the next publish that finds room reports the gap into the stream itself, so a
    /// missing stretch of transcript is visible to the operator rather than silent.
    /// </summary>
    public bool Publish(PluginSessionEvent sessionEvent)
    {
        if (!_channel.Writer.TryWrite(sessionEvent))
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }

        // Only once a real event got through, so the notice never takes a slot an event could use — writing it first
        // meant every freed slot at capacity went to a notice and the event behind it dropped again, a stream of
        // complaints instead of a transcript.
        _ReportAnyGap(sessionEvent.SessionId);
        return true;
    }

    /// <summary>
    /// Closes the stream, optionally with the error that ended it. Reports any outstanding gap first.
    /// </summary>
    public bool TryComplete(Exception? error = null)
    {
        _ReportAnyGap(sessionId: null);

        return _channel.Writer.TryComplete(error);
    }

    // Claimed only once the notice is actually in the channel: if there is still no room, the count stays and the
    // next publish tries again. Losing the count would be losing the only record that anything went missing.
    private void _ReportAnyGap(string? sessionId)
    {
        var dropped = Volatile.Read(ref _dropped);
        if (dropped == 0)
        {
            return;
        }

        var notice = new PluginSessionError
        {
            SessionId = sessionId ?? string.Empty,
            Message = $"{dropped} event(s) from this session were dropped: the cockpit fell more than {Capacity} events behind. The transcript above is missing that stretch.",
        };

        if (_channel.Writer.TryWrite(notice))
        {
            Interlocked.Add(ref _dropped, -dropped);
        }
    }
}
