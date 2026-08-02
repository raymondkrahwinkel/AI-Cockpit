using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Auditing;

// Names the audit trails, in one place, so the two sides that have to agree about them cannot drift apart: the
// logs that append to them (the classes deriving from `JsonlAuditLog{T}`) and the startup
// housekeeping that restricts the ones an earlier version created at the umask (AC-435). A trail whose path
// comes from here is repaired without anyone remembering a second edit; one that names its own file is not.
//
// The repair walks this fixed list rather than everything the base class touches, because a derived log can be
// pointed at an arbitrary file — a test's temp folder, say — and changing the mode of whatever a caller happened
// to name is not something that should follow from writing one line to it.
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
