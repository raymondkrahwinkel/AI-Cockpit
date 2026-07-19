using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Infrastructure.Consent;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Terminal;

/// <summary>
/// The <c>cockpit-terminal</c> MCP tools (AC-34): let an agent read and drive a terminal pane the operator has open,
/// live and with the operator watching. Exposed only while the Options master switch is on (the endpoint is not
/// advertised to a session otherwise), so for an agent the feature simply does not exist until it is deliberately
/// turned on.
/// <para>
/// The gate is the shared AC-47 consent broker: the first time a session touches a pane (read or send), the operator
/// gets one Approve/Deny prompt on that pane; approval couples the session to it (one agent per pane) and starts the
/// output capture — which begins at the coupling, never the earlier scrollback, so a secret that scrolled by before
/// does not leak. The operator keeps control throughout: they can type alongside and Disconnect at any time (an
/// interrupt then stops a running command), and the pane shows an "agent connected" bar while it is coupled.
/// </para>
/// </summary>
internal sealed class TerminalMcpTools
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly ITerminalAccessRegistry _registry;
    private readonly IConsentBroker? _consent;

    // The consent broker is optional so the tool's own tests construct it without a host; the container injects the
    // shared singleton, so a real access is gated behind an operator Approve/Deny that fails closed when nobody can ask.
    public TerminalMcpTools(ITerminalAccessRegistry registry, IConsentBroker? consent = null)
    {
        _registry = registry;
        _consent = consent;
    }

    [McpServerTool(Name = "list_terminals")]
    [Description("Lists the terminal panes the operator has open that you could ask to use: each with a stable id and the name the operator sees (e.g. \"zsh-5\"), and whether you are already coupled to it. Reading or driving a pane needs the operator to approve it first (see read_terminal / send_terminal); this list only names the panes so you can reference one.")]
    public string ListTerminals(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session)
    {
        var terminals = _registry.ListPanes(session)
            .Select(pane => new { id = pane.PaneId, name = pane.Name, coupled = pane.Coupled });
        return _Serialize(new { ok = true, terminals });
    }

    [McpServerTool(Name = "read_terminal")]
    [Description("Returns the output of a terminal pane the operator has open — you name it by the id or name from list_terminals (e.g. \"zsh-5\"). The first time you touch a pane the operator gets one Approve/Deny prompt on it; only after Approve do you get its output, and only what is printed from that moment on (never the earlier history). One agent at a time per pane. Read again to see newer output.")]
    public async Task<string> ReadTerminal(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The terminal to read, by its id or name from list_terminals, e.g. \"zsh-5\".")] string terminal)
    {
        if (_registry.Resolve(terminal) is not { } pane)
        {
            return _Serialize(new { ok = false, error = "No such terminal pane — call list_terminals for the open panes and their ids." });
        }

        if (await _EnsureCoupledAsync(session, pane).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        var output = _registry.ReadCoupled(session, pane.PaneId) ?? string.Empty;
        return _Serialize(new { ok = true, id = pane.PaneId, name = pane.Name, output });
    }

    [McpServerTool(Name = "send_terminal")]
    [Description("Types input into a terminal pane the operator has open — you name it by the id or name from list_terminals. Set submit=true to press Enter after it (run the line). To interrupt a running command send the text \"\\u0003\" (Ctrl-C). The first time you touch a pane the operator gets one Approve/Deny prompt on it; only after Approve can you type. The operator watches live and can type alongside or Disconnect at any time. One agent at a time per pane. Use read_terminal to see the result.")]
    public async Task<string> SendTerminal(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The terminal to type into, by its id or name from list_terminals, e.g. \"zsh-5\".")] string terminal,
        [Description("The text/keys to send. Control keys work too, e.g. \"\\u0003\" for Ctrl-C.")] string input,
        [Description("Press Enter after the input (run the line). Default false.")] bool submit = false)
    {
        if (_registry.Resolve(terminal) is not { } pane)
        {
            return _Serialize(new { ok = false, error = "No such terminal pane — call list_terminals for the open panes and their ids." });
        }

        if (await _EnsureCoupledAsync(session, pane).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        var bytes = Encoding.UTF8.GetBytes(submit ? input + "\r" : input);
        return _registry.SendInput(session, pane.PaneId, bytes)
            ? _Serialize(new { ok = true, id = pane.PaneId, name = pane.Name, sentBytes = bytes.Length })
            : _Serialize(new { ok = false, error = "The terminal could not be written to — it may have closed or been disconnected." });
    }

    /// <summary>
    /// Ensures this session holds the coupling on <paramref name="pane"/>, asking the operator for approval the first
    /// time. Returns an error string to surface, or null when the session is (now) coupled. Approval grants both
    /// reading and driving under one gate — the operator's live view and Disconnect are the counterpart to that.
    /// </summary>
    private async Task<string?> _EnsureCoupledAsync(string session, TerminalPane pane)
    {
        if (_registry.IsCoupledBy(session, pane.PaneId))
        {
            return null;
        }

        if (_registry.IsCoupledByAnother(session, pane.PaneId))
        {
            return $"Terminal pane \"{pane.Name}\" is already being driven by another agent — only one agent at a time can use a pane.";
        }

        if (_consent is null)
        {
            return "Using a terminal pane needs the operator's approval, which is not available here.";
        }

        var decision = await _consent.RequestConsentAsync(new ConsentRequest(
            "An agent wants to read and drive a terminal live",
            $"Give this agent live access to terminal pane {_SingleLine(pane.Name)}. It will see everything printed there from now on — not the earlier history — and can type into it (including Ctrl-C). You can watch, type alongside, and Disconnect at any time.",
            new ConsentSource(pane.PaneId, null, "Terminal MCP"),
            "terminal.couple",
            ConsentRisk.Dangerous)).ConfigureAwait(false);
        if (!decision.IsApproved)
        {
            return "Using that terminal was not approved by the operator.";
        }

        _registry.Couple(session, pane.PaneId);
        return null;
    }

    // Fold anything a consent surface could render as a line break out of the pane name before it goes verbatim into
    // the Dangerous prompt, so a crafted pane name cannot smuggle reassuring extra lines into what the operator
    // approves (cf. AC-80/AC-92). The Unicode line/paragraph/next-line separators (0x2028/0x2029/0x0085) are compared
    // numerically so no raw separator character sits in this source file.
    private static string _SingleLine(string value) =>
        new(value.Select(character =>
            char.IsControl(character) || character == 0x2028 || character == 0x2029 || character == 0x0085
                ? ' '
                : character).ToArray());

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
