using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Shared AC-134 helpers for an MCP-server checklist (the New-session dialog and the profile editor both use one):
/// estimating each row's tool tokens in the background, and rolling the ticked rows up into a labelled running
/// total. Kept UI-toolkit-free so it stays unit-testable; the caller owns the collection and marshals to the UI
/// thread (the estimate awaits resume on the captured dialog context).
/// </summary>
internal static class McpTokenEstimation
{
    /// <summary>
    /// Estimates each server in turn (the estimator caches, so re-runs are cheap unless <paramref name="refresh"/>),
    /// marking each row as estimating while its turn is in flight. A per-server failure lands as an unavailable
    /// estimate rather than stopping the rest.
    /// </summary>
    public static async Task EstimateAllAsync(
        IReadOnlyList<McpServerSelectionItemViewModel> items,
        IMcpToolTokenEstimator estimator,
        bool refresh,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            // A newer run (a refresh, or the dialog closing) supersedes this one — stop launching estimates rather
            // than churn through the rest; the row's flag is set only when its turn comes, so no row is left stuck
            // "estimating".
            cancellationToken.ThrowIfCancellationRequested();

            item.IsEstimatingTokens = true;
            try
            {
                item.TokenEstimate = await estimator.EstimateAsync(item.Name, refresh, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                item.IsEstimatingTokens = false;
                throw;
            }
            catch
            {
                item.TokenEstimate = McpServerToolEstimate.Unavailable(item.Name);
            }
            finally
            {
                item.IsEstimatingTokens = false;
            }
        }
    }

    /// <summary>The rolled-up figure over the <em>ticked</em> rows: the summed known tokens, whether any ticked row is still counting, and whether any ticked row's cost is unknown (a server that would not enumerate).</summary>
    public static (int Tokens, bool AnyEstimating, bool AnyUnknown) Total(IEnumerable<McpServerSelectionItemViewModel> items)
    {
        var tokens = 0;
        var anyEstimating = false;
        var anyUnknown = false;

        foreach (var item in items.Where(item => item.IsEnabledForSession))
        {
            if (item.IsEstimatingTokens)
            {
                anyEstimating = true;
            }
            else if (item.TokenEstimate is { Available: true } estimate)
            {
                tokens += estimate.EstimatedTokens;
            }
            else if (item.TokenEstimate is { Available: false })
            {
                anyUnknown = true;
            }
        }

        return (tokens, anyEstimating, anyUnknown);
    }

    /// <summary>The operator-facing summary line for the ticked rows, labelled clearly as a tools-only estimate.</summary>
    public static string SummaryLabel(IEnumerable<McpServerSelectionItemViewModel> items)
    {
        var (tokens, anyEstimating, anyUnknown) = Total(items);
        if (anyEstimating)
        {
            return "MCP tools: estimating…";
        }

        var line = $"MCP tools: ~{McpToolTokenMath.Format(tokens)} tokens (estimate, tools only)";
        return anyUnknown ? line + " + some unknown" : line;
    }
}
