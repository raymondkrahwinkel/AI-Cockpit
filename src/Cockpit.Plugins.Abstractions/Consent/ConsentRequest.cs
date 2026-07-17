namespace Cockpit.Plugins.Abstractions.Consent;

/// <summary>
/// Asks the operator to approve a single action before it happens — the one consent primitive shared by the
/// workflows plugin's dangerous steps, the terminal MCP, and anything else that acts with the operator's rights
/// on an agent's say-so. Passed to <c>ICockpitHost.RequestConsentAsync</c> (or the host's own broker) which
/// shows an Approve/Deny surface and returns the <see cref="ConsentDecision"/>.
/// <para>
/// <see cref="Action"/> is the ground truth: it must be the literal thing that will run — the actual command and
/// working directory, the actual URL, the pane being taken over — never a caller-composed summary of it. A
/// prompt-injected agent controls the words it supplies, so a gate that shows a friendly description of a hostile
/// command is a gate that approves the command. The surface renders <see cref="Action"/> verbatim; the caller's
/// job is to make it the truth, not to phrase it.
/// </para>
/// </summary>
/// <param name="Title">A short line naming what is being asked — "Workflow wants to run a command". Host-side framing, distinct from the untrusted <paramref name="Action"/>.</param>
/// <param name="Action">The literal action, shown verbatim and read-only: the real command + working directory, the real URL, the pane. Never a summary.</param>
/// <param name="Source">Who is asking (session, plugin) — see <see cref="ConsentSource"/>.</param>
/// <param name="Scope">A stable key for "remember this", identifying the kind of action (e.g. <c>workflow.http:GET</c>). Only consulted for a <see cref="ConsentRisk.LowRisk"/> request.</param>
/// <param name="Risk">Whether this is a <see cref="ConsentRisk.Dangerous"/> action (never remembered) or <see cref="ConsentRisk.LowRisk"/>.</param>
/// <param name="AllowRemember">Whether to offer "remember for this session". Honoured only when <paramref name="Risk"/> is <see cref="ConsentRisk.LowRisk"/>; ignored for a dangerous action.</param>
public sealed record ConsentRequest(
    string Title,
    string Action,
    ConsentSource Source,
    string Scope,
    ConsentRisk Risk,
    bool AllowRemember = false);
