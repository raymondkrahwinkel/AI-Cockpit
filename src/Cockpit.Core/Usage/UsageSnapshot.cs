namespace Cockpit.Core.Usage;

// What one session had run up at a moment in time (AC-251): the token buckets, the cost and the turns behind
// the header's meter, plus enough about the session to tell later which work they belong to. Written after every
// completed turn, so the newest record for a `PaneId` is that session's total — that the totals are
// carried on every record rather than only at the end is what makes them survive a crash or a kill, which is
// the whole point: until this existed the numbers lived only in memory and yesterday's spend was unrecoverable.
public sealed record UsageSnapshot
{
    // The session this belongs to — the cockpit's own pane id, unique to one session for as long as it exists.
    public string PaneId { get; init; } = string.Empty;

    // When the session's runtime went up — the start of its working life, which is what `Duration` measures from.
    public DateTimeOffset StartedAt { get; init; }

    // When this record was written — the moment the turn behind it settled.
    public DateTimeOffset RecordedAt { get; init; }

    // Whether the operator drove this session or a plugin did.
    public UsageRunKind RunKind { get; init; }

    // The run this session was embedded for, when a plugin said so (`EmbeddedSessionRequest.RunId`). An
    // Autopilot run spends across a session per step plus its CEO, so grouping on this is what turns a pile of
    // session records into "what that run cost". Null for a session that belongs to no run.
    public string? RunId { get; init; }

    // The run's name as the plugin knew it, so a costly `RunId` can be recognised without looking it up elsewhere.
    public string? RunLabel { get; init; }

    // The profile the session ran under — whose account the tokens came off.
    public string? ProfileLabel { get; init; }

    // The model in effect when this record was written. The totals are cumulative over the session, so a session
    // that switched models attributes the whole sum to the last one — read a per-model split off the records
    // before and after the switch rather than off a single one.
    public string? Model { get; init; }

    public int InputTokens { get; init; }

    public int OutputTokens { get; init; }

    public int CacheReadInputTokens { get; init; }

    public int CacheCreationInputTokens { get; init; }

    // What the provider said this cost so far, in US dollars. Zero where the provider reports no cost at all (a local model).
    public double TotalCostUsd { get; init; }

    // Completed turns behind these totals.
    public int Turns { get; init; }

    public int TotalTokens => InputTokens + OutputTokens + CacheReadInputTokens + CacheCreationInputTokens;

    // How long the session had been going when this was written. Measured to the turn that produced this record,
    // not to the pane being closed: a session that sat idle for an hour after its last turn did not work for that
    // hour, and a token baseline wants the working time.
    public TimeSpan Duration => RecordedAt - StartedAt;
}
