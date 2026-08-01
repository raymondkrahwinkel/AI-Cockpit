using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Folds a Claude SDK session's stdout into the provider-neutral <see cref="PluginSessionStatus"/> the header's
/// usage pill renders from (AC-530) — the SDK route's answer to what <see cref="ClaudeStatusLine"/> does for the
/// TTY route. Measured against CLI 2.1.220: a <c>claude</c> started with <c>--output-format stream-json</c> never
/// invokes the statusline command, so that relay is not merely unwired on this route but unavailable, and the two
/// figures have to be read off the stream itself.
/// <para>
/// Both are the provider's own numbers rather than an estimate this class invents:
/// </para>
/// <list type="bullet">
/// <item>
/// The rolling allowances arrive whole on the CLI's <c>rate_limit_event</c> line, whose <c>utilization</c> is the
/// very field the statusline multiplies by 100 for its own <c>used_percentage</c> — so a TTY and an SDK session
/// looking at the same account report the same figure from the same origin.
/// </item>
/// <item>
/// The context percentage is recomputed with the CLI's own formula over the CLI's own inputs: the token counts of
/// the <em>last</em> API call, over the context window size the <c>result</c> line states for the model that
/// answered. Deliberately <em>not</em> <c>result.usage</c>, which sums every API call in the turn — that total is
/// what the turn cost, not how full the window is, and on a four-call turn the two differ by more than 3× (11%
/// against the true 3%).
/// </item>
/// </list>
/// <para>
/// A figure the provider has not reported stays <see langword="null"/>/absent rather than reading as a zero, so the
/// header hides the segment instead of claiming nothing has been spent.
/// </para>
/// </summary>
/// <remarks>
/// Threading matches the Codex driver's template: the stdout pump is the only writer of the component fields, and
/// the immutable snapshot it builds is published to a volatile field so the host's poll — a different thread,
/// reading at each turn boundary — never sees a half-updated set.
/// </remarks>
internal sealed class ClaudeSdkUsage
{
    // DateTimeOffset.FromUnixTimeSeconds' own accepted range, asserted against the constants below in the tests so
    // this stays a guard rather than two numbers that drifted apart.
    private const long _MinEpochSeconds = -62135596800;
    private const long _MaxEpochSeconds = 253402300799;

    private readonly Dictionary<string, PluginRateLimitWindow> _windows = new(StringComparer.Ordinal);

    private long? _lastCallInputTokens;
    private string? _lastCallModel;
    private long? _contextWindowSize;

    private volatile PluginSessionStatus? _status;

    /// <summary>
    /// The latest snapshot, or <see langword="null"/> while the provider has reported neither figure — which is also
    /// what a session reports before its first turn settles.
    /// </summary>
    public PluginSessionStatus? Status => _status;

    /// <summary>
    /// Folds in the account-wide allowances read from the CLI's own cache (AC-549), which is where the SDK route
    /// gets a percentage at all: <c>rate_limit_event</c> names the window but withholds its fill until the account
    /// approaches it. Keyed on the same wire names, so a later event that <em>does</em> carry a figure replaces
    /// this one rather than sitting beside it under a second label.
    /// <para>
    /// Called from the stdout pump, which is this class's only writer — see the threading note above.
    /// </para>
    /// </summary>
    public void ObserveAccountWindows(IReadOnlyDictionary<string, PluginRateLimitWindow> windows)
    {
        var changed = false;
        foreach (var (key, window) in windows)
        {
            // An event-borne figure is the account's own reading for this very session and stays authoritative; the
            // cache only fills the gap where no figure has arrived.
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

    /// <summary>
    /// Folds one already-parsed stdout line in. Lines this class has no use for are ignored, so it can be handed
    /// every line without the caller classifying first.
    /// </summary>
    public void Observe(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
        {
            return;
        }

        switch (type.GetString())
        {
            case "assistant":
                _ObserveAssistant(root);
                break;
            case "result":
                _ObserveResult(root);
                break;
            case "rate_limit_event":
                _ObserveRateLimit(root);
                break;
            default:
                return;
        }

        _Publish();
    }

    // One assistant line is one API response, and its usage is that single call's — which is exactly the "last API
    // call" the CLI's own statusline measures the window with. A line stamped with parent_tool_use_id belongs to a
    // sub-agent running under a Task call: its context is its own, not this session's, so letting it through would
    // report a fresh sub-agent's near-empty window as the main conversation's. The CLI drops those lines from its
    // own consumer stream for the same reason.
    private void _ObserveAssistant(JsonElement root)
    {
        if (root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String)
        {
            return;
        }

        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // The three components of what was sent up for this call, the same sum the CLI forms. A usage object naming
        // none of them is a shape this build does not understand: keep the previous reading rather than folding it in
        // as a zero, which would render as "0% used".
        var present = false;
        var total = 0L;
        foreach (var field in (ReadOnlySpan<string>)["input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens"])
        {
            // A negative count is not a number this can mean anything by, so it is read as "not reported" rather than
            // folded in — clamping it to zero would turn nonsense into a confident "nothing spent".
            if (usage.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var tokens) && tokens >= 0)
            {
                present = true;
                total += tokens;
            }
        }

        // total only goes negative by overflowing, which takes counts no real window could hold; treat that the same
        // way as an unreadable figure rather than letting the wrap read as an empty context.
        if (!present || total < 0)
        {
            return;
        }

        _lastCallInputTokens = total;
        if (message.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
        {
            _lastCallModel = model.GetString();
        }
    }

    // The result line is the only place the CLI states the window size it measured against. Its own usage block is
    // read for nothing here on purpose (see the class remarks): that one is the turn's total.
    private void _ObserveResult(JsonElement root)
    {
        if (!root.TryGetProperty("modelUsage", out var modelUsage) || modelUsage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // This turn states its own models, so the window an earlier turn established has stopped speaking for it.
        // Dropping it before the lookup is what stops a stale denominator meeting this turn's fresh token count: that
        // pairing yields a percentage which is wrong and yet entirely plausible, which is worse than showing none.
        // A turn whose models this build cannot match therefore reports no context figure rather than an old one.
        _contextWindowSize = null;

        if (!_TryFindModelUsage(modelUsage, out var entry)
            || !entry.TryGetProperty("contextWindow", out var window) || window.ValueKind != JsonValueKind.Number
            || !window.TryGetInt64(out var size) || size <= 0)
        {
            return;
        }

        _contextWindowSize = size;
    }

    // Keyed by the model that actually answered, since a turn may name more than one and their windows differ. With
    // no such key — a model the result line spells differently from the assistant line — a single-entry map is still
    // unambiguous; anything else is left alone rather than guessed at.
    private bool _TryFindModelUsage(JsonElement modelUsage, out JsonElement entry)
    {
        if (_lastCallModel is { Length: > 0 } model
            && modelUsage.TryGetProperty(model, out entry) && entry.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        entry = default;
        var found = false;
        foreach (var property in modelUsage.EnumerateObject())
        {
            if (found || property.Value.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            entry = property.Value;
            found = true;
        }

        return found;
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
        // real stream at 2% of the five-hour allowance, the event carries status, resetsAt and rateLimitType and no
        // figure at all (AC-549). That used to drop the whole event, and with it the reset time, which is knowledge
        // this line does have. The percentage then comes from ClaudeUsageCache instead; leaving whatever it already
        // published in place is the point of returning rather than overwriting.
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

    // The CLI's own statusline arithmetic (2.1.220), kept identical down to the rounding so the same session reads the
    // same whichever route shows it: round half away from zero — .NET's default is banker's rounding, which would
    // disagree with JavaScript's Math.round on every exact half — then clamp into 0-100.
    private double? _ContextUsedPercent()
    {
        if (_lastCallInputTokens is not { } tokens || _contextWindowSize is not { } size)
        {
            return null;
        }

        var percent = Math.Round(tokens / (double)size * 100, MidpointRounding.AwayFromZero);
        return Math.Clamp(percent, 0, 100);
    }

    private void _Publish()
    {
        // Ordered so the pill reads ctx · 5h · wk however the lines happened to arrive; a window this build has no
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

        var status = new PluginSessionStatus(_ContextUsedPercent(), windows);
        _status = status.HasAny ? status : null;
    }
}
