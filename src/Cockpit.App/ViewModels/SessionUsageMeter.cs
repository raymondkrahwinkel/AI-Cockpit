using System.Globalization;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Accumulates the token usage and cost a session has run up (#8 token/cost meter), and formats it for the
/// compact meter next to the session status.
/// <para>
/// The two halves of a <see cref="TurnCompleted"/> result are counted differently, because the CLI reports
/// them differently: <c>usage</c> covers only the turn that just finished, so it sums, while
/// <c>total_cost_usd</c> is what the session has cost so far, so it replaces the previous figure. Summing
/// the cost charged every earlier turn again, which grew with the turn count — the nine-turn session in the
/// pilot run recorded $29.23 against a real $5.38. Measured against a real <c>claude</c> run rather than
/// assumed.
/// </para>
/// <para>
/// Following the newest figure is the whole story because a meter only ever counts one conversation:
/// <c>SessionViewModel.StartConfiguredAsync</c> returns early while a runtime exists, so the only way one
/// panel reaches a second CLI process is <c>ClearContextAsync</c> (AC-564) — which calls <see cref="Reset"/>
/// on the way, since the new process starts its own <c>total_cost_usd</c> back at zero. There is therefore
/// no earlier process whose spend could need carrying over, and no attempt is made to invent one from the
/// numbers.
/// </para>
/// </summary>
internal sealed class SessionUsageMeter
{
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public int CacheReadInputTokens { get; private set; }
    public int CacheCreationInputTokens { get; private set; }

    /// <summary>The newest session-so-far cost the provider reported, which is the session's cost.</summary>
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
            TotalCostUsd = cost;
        }

        Turns++;
    }

    /// <summary>Back to zero for a conversation that starts over in the same pane (AC-564's context clear).</summary>
    public void Reset()
    {
        InputTokens = 0;
        OutputTokens = 0;
        CacheReadInputTokens = 0;
        CacheCreationInputTokens = 0;
        TotalCostUsd = 0;
        Turns = 0;
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
