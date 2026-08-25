using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Auditing;

// Names the audit trails in one place, so the loggers (AC-435) and the startup housekeeping
// that repairs their umask permissions cannot drift apart. Repair walks this fixed list rather
// than every file a derived log touches, so a test's temp file is never chmod'd by accident.
internal static class AuditTrailFiles
{
    // The consent trail (#AC-47): the commands an operator approved.
    public const string Consent = "consent-audit.jsonl";

    // The delegation trail (#67): the prompts sub-agents were given.
    public const string Delegation = "delegation-audit.jsonl";

    // The usage trail (AC-251): what the sessions spent.
    public const string Usage = "usage-history.jsonl";

    // The agent-notify trail (AC-392): the messages agents sent each other.
    public const string AgentNotify = "agent-notify-audit.jsonl";

    // The assistant spawn trail (AC-545): every session the assistant asked the host to start or stop.
    public const string AssistantSpawn = "assistant-spawn-audit.jsonl";

    // Every trail's file name. The order carries no meaning.
    public static IReadOnlyList<string> Names { get; } = [Consent, Delegation, Usage, AgentNotify, AssistantSpawn];

    // Where `fileName` lives for this install: next to `cockpit.json`, under the state root
    // a development build keeps to itself.
    public static string InStateRoot(string fileName) => Path.Combine(CockpitConfigPath.Root, fileName);

    // The trails as they would live in `directory`. Existence is not implied — a cockpit that has
    // never asked for consent has no consent trail, and a caller that acts on these paths handles that.
    public static IEnumerable<string> In(string directory) =>
        Names.Select(name => Path.Combine(directory, name));
}
