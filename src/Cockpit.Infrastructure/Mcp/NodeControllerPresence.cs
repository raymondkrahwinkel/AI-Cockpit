using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// AC-1321: derived from the last authorized controller call rather than from a heartbeat — there is none, and the
// controller cockpit's own 20s node poll (AC-796) is what keeps the line open. Touched at the one door every
// controller call passes (`CockpitMcpEndpointHost._AuthorizeAsync`), so part d's control tools count by themselves.
internal sealed class NodeControllerPresence : INodeControllerPresence, ISingletonService
{
    // Three missed 20s polls: one slow or dropped poll must not flicker the node's assistant off and on.
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private ActiveController? _current;
    private DateTimeOffset _lastSeenUtc;
    private ITimer? _expiry;

    public NodeControllerPresence()
        : this(TimeProvider.System)
    {
    }

    // Test seam: a controllable clock, so the fall-back is provable without waiting a minute for it.
    internal NodeControllerPresence(TimeProvider time)
    {
        _time = time;
    }

    public event EventHandler? Changed;

    // AC-1327 reviewbevinding 1: an unpair ends the coupling at once, so Current must clear at once too, not wait
    // out the minute-long expiry. Called post-construction (`CockpitMcpEndpointHost.StartAsync`) rather than a
    // constructor dependency, which would pull the broker's whole endpoint-hosting chain into every resolution here.
    internal void WatchPairing(INodePairingBroker pairing)
    {
        pairing.Changed += (_, _) =>
        {
            if (pairing.Pairing is null)
            {
                _Clear();
            }
        };
    }

    public ActiveController? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    // ponytail: one controller at a time is assumed, not enforced (epic point 6 is open) — two machines calling
    // within the window take turns owning the name here. Key on the caller if Raymond decides otherwise.
    public void Seen(string controllerName)
    {
        bool appeared;
        lock (_gate)
        {
            appeared = _current is null;
            _lastSeenUtc = _time.GetUtcNow();
            _current ??= new ActiveController(controllerName, _lastSeenUtc);
            _expiry?.Dispose();
            _expiry = _time.CreateTimer(_ => _Expire(), null, Window, Timeout.InfiniteTimeSpan);
        }

        if (appeared)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void _Expire()
    {
        lock (_gate)
        {
            // A timer already firing when `Seen` replaced it must not undo the call that replaced it.
            if (_current is null || _time.GetUtcNow() - _lastSeenUtc < Window)
            {
                return;
            }

            _current = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // AC-1327 reviewbevinding 1: the unpair-driven clear — skips the window check `_Expire` makes, since an
    // unpair is not a missed poll, it is the coupling ending right now.
    private void _Clear()
    {
        bool disappeared;
        lock (_gate)
        {
            disappeared = _current is not null;
            _current = null;
            _expiry?.Dispose();
            _expiry = null;
        }

        if (disappeared)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
