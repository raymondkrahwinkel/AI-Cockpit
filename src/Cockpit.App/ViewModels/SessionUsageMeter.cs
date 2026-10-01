using System.Globalization;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// The meter over a session's running totals: an SDK pane takes them from its host's fold (AC-1437), a TTY pane sums
// its own readings. How the two halves of a result count is `SessionUsageTotals.Add`'s (AC-564).
internal sealed class SessionUsageMeter
{
    public SessionUsageTotals Totals { get; set; } = SessionUsageTotals.None;

    public int InputTokens => Totals.InputTokens;
    public int OutputTokens => Totals.OutputTokens;
    public int CacheReadInputTokens => Totals.CacheReadInputTokens;
    public int CacheCreationInputTokens => Totals.CacheCreationInputTokens;

    // The newest session-so-far cost the provider reported, which is the session's cost.
    public double TotalCostUsd => Totals.TotalCostUsd;

    // Completed turns counted into the meter (a turn is counted even when its result carried no usage).
    public int Turns => Totals.Turns;

    public int TotalTokens => Totals.TotalTokens;

    // True once anything worth showing has accrued, so a pure-error session with no usage keeps the meter hidden.
    public bool HasData => Totals.HasData;

    public void Add(TokenUsage? usage, double? costUsd) => Totals = Totals.Add(usage, costUsd);

    // Back to zero for a conversation that starts over in the same pane (AC-564's context clear).
    public void Reset() => Totals = SessionUsageTotals.None;

    // Compact one-line meter, e.g. `45.2k tok · $0.0123` — the cost is dropped when the provider reports none (local models).
    public string Summary =>
        TotalCostUsd > 0
            ? $"{FormatTokens(TotalTokens)} tok · {FormatCost(TotalCostUsd)}"
            : $"{FormatTokens(TotalTokens)} tok";

    // Per-bucket breakdown for the meter's hover text.
    public string Tooltip =>
        $"Input {FormatTokens(InputTokens)} · Output {FormatTokens(OutputTokens)} · " +
        $"Cache read {FormatTokens(CacheReadInputTokens)} · Cache write {FormatTokens(CacheCreationInputTokens)}" +
        (TotalCostUsd > 0 ? $" · {FormatCost(TotalCostUsd)}" : string.Empty) +
        $" · {Turns} turn{(Turns == 1 ? string.Empty : "s")}";

    // 950 → "950", 45210 → "45.2k", 2_300_000 → "2.30M": one glanceable number that never runs long.
    internal static string FormatTokens(int tokens) => tokens switch
    {
        < 1_000 => tokens.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => (tokens / 1_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => (tokens / 1_000_000.0).ToString("0.00", CultureInfo.InvariantCulture) + "M",
    };

    // Sub-dollar sessions need the extra digits to not read as "$0.00"; a dollar or more only needs cents.
    internal static string FormatCost(double costUsd) =>
        "$" + (costUsd < 1
            ? costUsd.ToString("0.0000", CultureInfo.InvariantCulture)
            : costUsd.ToString("0.00", CultureInfo.InvariantCulture));
}
