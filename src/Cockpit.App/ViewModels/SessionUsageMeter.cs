using System.Globalization;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Accumulates the token usage and cost a session has run up (#8 token/cost meter). Each
/// <see cref="TurnCompleted"/> result carries the usage and <c>total_cost_usd</c> for that one turn
/// (the CLI emits one <c>result</c> per user prompt), so a session total is simply their running sum —
/// this holds that sum and formats it for the compact meter next to the session status.
/// </summary>
internal sealed class SessionUsageMeter
{
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public int CacheReadInputTokens { get; private set; }
    public int CacheCreationInputTokens { get; private set; }
    public double TotalCostUsd { get; private set; }

    /// <summary>Completed turns counted into the meter (a turn is counted even when its result carried no usage).</summary>
    public int Turns { get; private set; }

    public int TotalTokens => InputTokens + OutputTokens + CacheReadInputTokens + CacheCreationInputTokens;

    /// <summary>True once anything worth showing has accrued, so a pure-error session with no usage keeps the meter hidden.</summary>
    public bool HasData => TotalTokens > 0 || TotalCostUsd > 0;

    /// <summary>Fold one completed turn's reported usage and cost into the running totals. Nulls (an error result with no usage) contribute nothing but still count as a turn.</summary>
    public void Add(TokenUsage? usage, double? costUsd)
    {
        if (usage is not null)
        {
            InputTokens += usage.InputTokens;
            OutputTokens += usage.OutputTokens;
            CacheReadInputTokens += usage.CacheReadInputTokens;
            CacheCreationInputTokens += usage.CacheCreationInputTokens;
        }

        if (costUsd is { } cost)
        {
            TotalCostUsd += cost;
        }

        Turns++;
    }

    /// <summary>Compact one-line meter, e.g. <c>45.2k tok · $0.0123</c> — the cost is dropped when the provider reports none (local models).</summary>
    public string Summary =>
        TotalCostUsd > 0
            ? $"{FormatTokens(TotalTokens)} tok · {FormatCost(TotalCostUsd)}"
            : $"{FormatTokens(TotalTokens)} tok";

    /// <summary>Per-bucket breakdown for the meter's hover text.</summary>
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
