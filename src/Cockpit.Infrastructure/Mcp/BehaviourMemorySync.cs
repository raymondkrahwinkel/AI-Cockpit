using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Assistant;

namespace Cockpit.Infrastructure.Mcp;

// AC-1330: Raymond's decision (2026-09-18) — mutual append of behaviour rules at the reachable edge, never a
// queue and never the machine file (AC-1328 stays untouched). Rides the same edge `NodeInboxRelay.NotifyTransition`
// rides, plus once at launch, since a card's very first successful read is not a transition either.
public sealed class BehaviourMemorySync(
    INodeSessionsClient nodes,
    IAssistantMemory memory,
    IAgentMessageInbox inbox,
    ILogger<BehaviourMemorySync>? logger = null) : ISingletonService
{
    private readonly ILogger<BehaviourMemorySync> _logger = logger ?? NullLogger<BehaviourMemorySync>.Instance;

    // ` (from LAPTOP, 2026-09-16)` — the origin suffix a taken-over line carries. Matched at the end of the line
    // so a rule whose own text happens to contain "from" elsewhere is not mistaken for one.
    private static readonly Regex _OriginSuffix = new(
        @" \(from (?<machine>[^,()]+), (?<date>\d{4}-\d{2}-\d{2})\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // `AssistantMemoryFile.RememberAsync` writes every entry as `- {date} — {text}` under a heading (AC-1330
    // review) — stripped here so the sync compares and re-sends the rule text, not that file's own bullet.
    private static readonly Regex _DatePrefix = new(
        @"^- \d{4}-\d{2}-\d{2} — ", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task RunAsync(string nodeName, CancellationToken cancellationToken = default)
    {
        var nodeRead = await nodes.ReadMemoryAsync(nodeName, "behaviour", cancellationToken).ConfigureAwait(false);
        if (nodeRead.Error is { Length: > 0 } readError)
        {
            // A half read must never turn into an append — not on the node (nothing to compare against) and not
            // locally either, or a line the node still has under a different wording could be taken over twice.
            _logger.LogDebug("Behaviour sync with {Node} skipped: {Error}", nodeName, readError);
            return;
        }

        var ownMachine = Environment.MachineName;
        var localLines = _Entries(await memory.ReadAsync(AssistantMemoryScope.Behaviour, cancellationToken).ConfigureAwait(false));
        var nodeLines = _Entries(nodeRead.Text ?? "");
        var localCores = localLines.Select(_StripOrigin).ToHashSet(StringComparer.Ordinal);
        var nodeCores = nodeLines.Select(_StripOrigin).ToHashSet(StringComparer.Ordinal);

        // Never re-take a line that already names this machine as its origin — it was sent there by us, and taking
        // it back would pingpong even after the operator prunes the node's own copy.
        var toTakeOver = nodeLines
            .Where(line => !_CameFrom(line, ownMachine) && !localCores.Contains(_StripOrigin(line)))
            .Select(_StripOrigin)
            .ToList();
        var toSend = localLines
            .Where(line => !_CameFrom(line, nodeName) && !nodeCores.Contains(_StripOrigin(line)))
            .Select(_StripOrigin)
            .ToList();

        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var rule in toTakeOver)
        {
            await memory.RememberAsync($"{rule} (from {nodeName}, {today})", AssistantMemoryScope.Behaviour, cancellationToken).ConfigureAwait(false);
        }

        var sent = 0;
        string? sendError = null;
        foreach (var rule in toSend)
        {
            var reason = await nodes.RememberOnNodeAsync(nodeName, $"{rule} (from {ownMachine}, {today})", "behaviour", cancellationToken).ConfigureAwait(false);
            if (reason is null)
            {
                sent++;
            }
            else
            {
                sendError ??= reason;
            }
        }

        if (toTakeOver.Count + sent == 0)
        {
            return;
        }

        var body = $"Took over {toTakeOver.Count} behaviour rules from {nodeName} and sent {sent} there.";
        if (sendError is not null)
        {
            body += $" Not all reached {nodeName}: {sendError}";
        }

        inbox.Deliver(nodeName, AssistantIdentity.PaneId, "behaviour-sync", body);
    }

    // Only a `- ` line is a rule — the heading and the blank line under it are structure, not content, and must
    // never round-trip as if the operator had written a rule that says "# What the operator asked me to remember".
    private static List<string> _Entries(string text) =>
        [.. text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(_StripDatePrefix)];

    private static string _StripDatePrefix(string entry)
    {
        var match = _DatePrefix.Match(entry);
        return match.Success ? entry[match.Length..] : entry;
    }

    private static string _StripOrigin(string line)
    {
        var match = _OriginSuffix.Match(line);
        return match.Success ? line[..match.Index] : line;
    }

    private static bool _CameFrom(string line, string machine)
    {
        var match = _OriginSuffix.Match(line);
        return match.Success && string.Equals(match.Groups["machine"].Value, machine, StringComparison.Ordinal);
    }
}
