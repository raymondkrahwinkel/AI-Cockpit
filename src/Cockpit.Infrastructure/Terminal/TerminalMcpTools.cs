using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Infrastructure.Consent;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Terminal;

/// <summary>
/// The <c>cockpit-terminal</c> MCP tools (AC-34, phase 1): let an agent read a terminal pane the operator has open,
/// live and with the operator watching. Exposed only while the Options master switch is on (the endpoint is not
/// advertised to a session otherwise), so for an agent the feature simply does not exist until it is deliberately
/// turned on.
/// <para>
/// The gate is the shared AC-47 consent broker: the first time a session asks to read a pane, the operator gets an
/// Approve/Deny prompt on that pane; approval couples the session to it (one agent per pane) and starts the output
/// capture — which begins at the coupling, never the earlier scrollback, so a secret that scrolled by before does not
/// leak. A later read by the same session does not re-prompt. Sending keystrokes is phase 2.
/// </para>
/// </summary>
internal sealed class TerminalMcpTools
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly ITerminalAccessRegistry _registry;
    private readonly IConsentBroker? _consent;

    // The consent broker is optional so the tool's own tests construct it without a host; the container injects the
    // shared singleton, so a real read is gated behind an operator Approve/Deny that fails closed when nobody can ask.
    public TerminalMcpTools(ITerminalAccessRegistry registry, IConsentBroker? consent = null)
    {
        _registry = registry;
        _consent = consent;
    }

    [McpServerTool(Name = "list_terminals")]
    [Description("Lists the terminal panes the operator has open that you could ask to read: each with a stable id and the name the operator sees (e.g. \"zsh-5\"), and whether you are already coupled to it. Reading a pane's output needs the operator to approve it first (see read_terminal); this list only names the panes so you can reference one.")]
    public string ListTerminals(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session)
    {
        var terminals = _registry.ListPanes(session)
            .Select(pane => new { id = pane.PaneId, name = pane.Name, coupled = pane.Coupled });
        return _Serialize(new { ok = true, terminals });
    }

    [McpServerTool(Name = "read_terminal")]
    [Description("Returns the output of a terminal pane the operator has open — you name it by the id or name from list_terminals (e.g. \"zsh-5\"). The first time you read a pane the operator gets an Approve/Deny prompt on it; only after Approve do you get its output, and only what is printed from that moment on (never the earlier history). One agent at a time per pane. Read again to see newer output.")]
    public async Task<string> ReadTerminal(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The terminal to read, by its id or name from list_terminals, e.g. \"zsh-5\".")] string terminal)
    {
        if (_registry.Resolve(terminal) is not { } pane)
        {
            return _Serialize(new { ok = false, error = "No such terminal pane — call list_terminals for the open panes and their ids." });
        }

        if (!_registry.IsCoupledBy(session, pane.PaneId))
        {
            if (_registry.IsCoupledByAnother(session, pane.PaneId))
            {
                return _Serialize(new { ok = false, error = $"Terminal pane \"{pane.Name}\" is already being driven by another agent — only one agent at a time can read a pane." });
            }

            if (_consent is null)
            {
                return _Serialize(new { ok = false, error = "Reading a terminal pane needs the operator's approval, which is not available here." });
            }

            var decision = await _consent.RequestConsentAsync(new ConsentRequest(
                "An agent wants to read a terminal live",
                $"Give this agent live read access to terminal pane {_SingleLine(pane.Name)}. It will see everything printed there from now on — not the earlier history. It cannot type into it (phase 1 is read-only).",
                new ConsentSource(pane.PaneId, null, "Terminal MCP"),
                "terminal.couple",
                ConsentRisk.Dangerous));
            if (!decision.IsApproved)
            {
                return _Serialize(new { ok = false, error = "Reading that terminal was not approved by the operator." });
            }

            _registry.Couple(session, pane.PaneId);
        }

        var output = _registry.ReadCoupled(session, pane.PaneId) ?? string.Empty;
        return _Serialize(new { ok = true, id = pane.PaneId, name = pane.Name, output });
    }

    // Fold anything a consent surface could render as a line break out of the pane name before it goes verbatim into
    // the Dangerous prompt, so a crafted pane name cannot smuggle reassuring extra lines into what the operator
    // approves (cf. AC-80/AC-92, the same flattening the worktree and cluster gates apply). The Unicode
    // line/paragraph/next-line separators (0x2028/0x2029/0x0085) are compared numerically so no raw separator
    // character sits in this source file.
    private static string _SingleLine(string value) =>
        new(value.Select(character =>
            char.IsControl(character) || character == 0x2028 || character == 0x2029 || character == 0x0085
                ? ' '
                : character).ToArray());

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
