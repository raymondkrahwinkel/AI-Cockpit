using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Auditing;

/// <summary>
/// Names the audit trails, in one place, so the two sides that have to agree about them cannot drift apart: the
/// logs that append to them (the classes deriving from <see cref="JsonlAuditLog{T}"/>) and the startup
/// housekeeping that restricts the ones an earlier version created at the umask (AC-435). A trail whose path
/// comes from here is repaired without anyone remembering a second edit; one that names its own file is not.
/// <para>
/// The repair walks this fixed list rather than everything the base class touches, because a derived log can be
/// pointed at an arbitrary file — a test's temp folder, say — and changing the mode of whatever a caller happened
/// to name is not something that should follow from writing one line to it.
/// </para>
/// </summary>
internal static class AuditTrailFiles
{
    /// <summary>The consent trail (#AC-47): the commands an operator approved.</summary>
    public const string Consent = "consent-audit.jsonl";

    /// <summary>The delegation trail (#67): the prompts sub-agents were given.</summary>
    public const string Delegation = "delegation-audit.jsonl";

    /// <summary>The usage trail (AC-251): what the sessions spent.</summary>
    public const string Usage = "usage-history.jsonl";

    /// <summary>The agent-notify trail (AC-392): the messages agents sent each other.</summary>
    public const string AgentNotify = "agent-notify-audit.jsonl";

    /// <summary>Every trail's file name. The order carries no meaning.</summary>
    public static IReadOnlyList<string> Names { get; } = [Consent, Delegation, Usage, AgentNotify];

    /// <summary>
    /// Where <paramref name="fileName"/> lives for this install: next to <c>cockpit.json</c>, under the state root
    /// a development build keeps to itself.
    /// </summary>
    public static string InStateRoot(string fileName) => Path.Combine(CockpitConfigPath.Root, fileName);

    /// <summary>
    /// The trails as they would live in <paramref name="directory"/>. Existence is not implied — a cockpit that has
    /// never asked for consent has no consent trail, and a caller that acts on these paths handles that.
    /// </summary>
    public static IEnumerable<string> In(string directory) =>
        Names.Select(name => Path.Combine(directory, name));
}
