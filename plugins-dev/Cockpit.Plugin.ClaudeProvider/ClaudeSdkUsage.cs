using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// Folds a Claude SDK session's figures into the provider-neutral `PluginSessionStatus` the header's usage pill
// renders from (AC-530) — the SDK route's answer to `ClaudeStatusLine`, which `--output-format stream-json`
// never invokes.
//
// Both figures are the CLI's own, asked for over the control channel: allowances from `get_usage` (see
// `ClaudeUsageWindows`), context from `get_context_usage`. Nothing here computes a percentage. The context
// figure used to be derived from the last assistant line's tokens over the result line's window size, which
// broke on 2.1.226 — assistant says `claude-opus-5`, result keys its `modelUsage` `claude-opus-5[1m]`, so any
// turn touching two models matched nothing. An unreported figure stays absent rather than reading as zero.
//
// Two writers on different threads (the stdout pump and the poll's continuation), hence the lock; the snapshot
// goes to a volatile field so the host's turn-boundary read never sees half a set.
internal sealed class ClaudeSdkUsage
{
    // DateTimeOffset.FromUnixTimeSeconds' own accepted range, asserted against the constants below in the tests so
    // this stays a guard rather than two numbers that drifted apart.
    private const long _MinEpochSeconds = -62135596800;
    private const long _MaxEpochSeconds = 253402300799;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, PluginRateLimitWindow> _windows = new(StringComparer.Ordinal);

    private double? _contextUsedPercent;

    private volatile PluginSessionStatus? _status;

    // The latest snapshot, or `null` while the CLI has reported neither figure — which is also
    // what a session reports before its first turn settles.
    public PluginSessionStatus? Status => _status;

    // Folds in the account-wide allowances from a `get_usage` reply. Keyed on the same wire names as
    // `rate_limit_event`, so a later event that carries a figure of its own replaces this one rather than
    // sitting beside it under a second label.
    public void ObserveAccountWindows(IReadOnlyDictionary<string, PluginRateLimitWindow> windows)
    {
        lock (_gate)
        {
            var changed = false;
            foreach (var (key, window) in windows)
            {
                if (_windows.TryGetValue(key, out var existing) && existing == window)
                {
                    continue;
                }

                _windows[key] = window;
                changed = true;
            }

            if (changed)
            {
                _Publish();
            }
        }
    }

    // Folds in a `get_context_usage` reply — the CLI's own `percentage`, the figure `/context` prints. A reply
    // without one leaves the previous reading standing rather than blanking the segment.
    public void ObserveContextUsage(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("percentage", out var percentage)
            || percentage.ValueKind != JsonValueKind.Number
            || !percentage.TryGetDouble(out var percent)
            || !double.IsFinite(percent))
        {
            return;
        }

        lock (_gate)
        {
            _contextUsedPercent = Math.Clamp(percent, 0, 100);
            _Publish();
        }
    }

    // Folds one already-parsed stdout line in. Lines this class has no use for are ignored, so it can be handed
    // every line without the caller classifying first.
    public void Observe(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || type.GetString() is not "rate_limit_event")
        {
            return;
        }

        lock (_gate)
        {
            _ObserveRateLimit(root);
            _Publish();
        }
    }

    // The CLI restates a window whenever its figure changes, so the newest line for a type replaces the previous one
    // and a type never seen simply has no segment.
    private void _ObserveRateLimit(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit_info", out var info) || info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("rateLimitType", out var wireType) || wireType.ValueKind != JsonValueKind.String
            || wireType.GetString() is not { Length: > 0 } key)
        {
            return;
        }

        // utilization is absent on this line whenever the account is not near the window it names — captured from a
        // real stream at 5% of the five-hour allowance, the event carries status, resetsAt and rateLimitType and no
        // figure at all. The percentage then comes from `get_usage` instead; leaving whatever it already published
        // in place is the point of returning rather than overwriting.
        if (!info.TryGetProperty("utilization", out var utilization) || utilization.ValueKind != JsonValueKind.Number
            || !utilization.TryGetDouble(out var fraction))
        {
            return;
        }

        // Past the allowance is real and must show; below zero is not a percentage at all. The finiteness check is on
        // the scaled figure rather than the raw one because the overflow happens in the multiply — 1e308 is itself a
        // perfectly finite double, and only becomes an infinity once it is a percentage, which would then reach the
        // header as "∞%" and size a bar against nothing.
        var usedPercent = fraction * 100;
        if (!double.IsFinite(usedPercent) || usedPercent < 0)
        {
            return;
        }

        // resetsAt is epoch seconds when present; a window the CLI reports without one still counts, it just cannot
        // offer a scheduled resume. Range-checked rather than handed straight to FromUnixTimeSeconds, which throws
        // outside its own bounds — and this runs on the stdout pump, where a throw ends the pump and takes the whole
        // session down over one unreadable timestamp.
        DateTimeOffset? resetsAt = info.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number
            && reset.TryGetInt64(out var epochSeconds)
            && epochSeconds is >= _MinEpochSeconds and <= _MaxEpochSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(epochSeconds)
            : null;

        // Left unclamped exactly as the statusline route leaves it: an account past its allowance genuinely reports
        // more than 100%, and rounding that down to a tidy 100 would hide the overage the operator most needs to see.
        _windows[key] = new PluginRateLimitWindow(
            ClaudeUsageSignals.WindowLabel(key),
            usedPercent,
            resetsAt,
            WindowMinutes: null);
    }

    private void _Publish()
    {
        // Ordered so the pill reads ctx · 5h · wk however the replies happened to arrive; a window this build has no
        // declaration for sorts last rather than displacing the two the operator knows.
        var windows = _windows.Values
            .OrderBy(window => window.Label switch
            {
                _ when string.Equals(window.Label, ClaudeUsageSignals.WindowLabel(ClaudeUsageSignals.FiveHourWireType), StringComparison.Ordinal) => 0,
                _ when string.Equals(window.Label, ClaudeUsageSignals.WindowLabel(ClaudeUsageSignals.WeeklyWireType), StringComparison.Ordinal) => 1,
                _ => 2,
            })
            .ThenBy(window => window.Label, StringComparer.Ordinal)
            .ToArray();

        var status = new PluginSessionStatus(_contextUsedPercent, windows);
        _status = status.HasAny ? status : null;
    }
}
