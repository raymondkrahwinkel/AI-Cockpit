using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Terminal;

/// <summary>
/// The <c>cockpit-terminal</c> MCP tools (AC-34): let an agent read and drive a terminal pane the operator has open,
/// live and with the operator watching. Exposed only while the Options master switch is on (the endpoint is not
/// advertised to a session otherwise), so for an agent the feature simply does not exist until it is deliberately
/// turned on.
/// <para>
/// The gate is the shared AC-47 consent broker, and it asks for the narrower thing: <c>read_terminal</c> asks to watch
/// a pane, <c>send_terminal</c> asks to type into it. So watching a build finish never quietly comes with the keyboard,
/// and an agent that only ever reads is never approved for more than reading. Approval couples the session to the pane
/// (one agent per pane) and starts the output capture — which begins at the coupling, never the earlier scrollback, so
/// a secret that scrolled by before does not leak. The operator keeps control throughout: they can type alongside and
/// Disconnect at any time, and the pane shows a bar saying which of the two an agent holds.
/// </para>
/// </summary>
internal sealed class TerminalMcpTools
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>Ceiling on how long <c>run_in_terminal</c> will wait, so an agent cannot park a request on the pane indefinitely.</summary>
    private const int MaxRunTimeoutSeconds = 600;

    /// <summary>How often the wait re-reads the shell state. Short enough to feel immediate, long enough not to spin on the registry lock.</summary>
    private const int PollIntervalMs = 100;

    /// <summary>Stands in for a read that came back null — the coupling ended between the check and the read. Nothing to report, and nothing was cut off.</summary>
    private static readonly TerminalCapturedOutput NothingCaptured = new(string.Empty, Truncated: false);

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
    [Description("Lists the shell panes the operator has open that you could ask to use: each with a stable id and the name the operator sees (e.g. \"zsh-5\"), and whether you are already coupled to it. Only the operator's shell panes are offered — a pane the cockpit started as an agent session is not one of them, whatever its name. Reading or driving a pane needs the operator to approve it first (see read_terminal / send_terminal); this list only names the panes so you can reference one.")]
    public string ListTerminals(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session)
    {
        // Key on the transport-verified pane (AC-89), not the agent-declared `session`: an agent must not be able to
        // read another session's coupling by naming its id (confused deputy). Falls back to `session` off the verified
        // path (the in-process tool loop / tests), where there is no middleware to trust.
        var caller = McpRequestContext.CurrentPaneId ?? session;
        var terminals = _registry.ListPanes(caller)
            .Select(pane => new
            {
                id = pane.PaneId,
                name = pane.Name,
                coupled = pane.Coupling is not null,
                mayType = pane.Coupling == TerminalCouplingMode.Drive,
            });
        return _Serialize(new { ok = true, terminals });
    }

    [McpServerTool(Name = "read_terminal")]
    [Description("Returns the output of a terminal pane the operator has open — you name it by the id or name from list_terminals (e.g. \"zsh-5\"). The first time you read a pane the operator gets an Approve/Deny prompt asking to let you watch it; only after Approve do you get its output, and only what is printed from that moment on (never the earlier history). Reading does not let you type — send_terminal asks for that separately. One agent at a time per pane. Read again to see newer output.")]
    public async Task<string> ReadTerminal(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The terminal to read, by its id or name from list_terminals, e.g. \"zsh-5\".")] string terminal)
    {
        if (_registry.Resolve(terminal) is not { } pane)
        {
            return _Serialize(new { ok = false, error = "No such terminal pane — call list_terminals for the open panes and their ids." });
        }

        var caller = McpRequestContext.CurrentPaneId ?? session;
        if (await _EnsureCoupledAsync(caller, pane, TerminalCouplingMode.Watch).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        // AC-34: strip the ANSI/VT escapes from the captured bytes so the agent reads plain text, not colour codes.
        // Stripped over the whole buffer, so a sequence split across pty writes is already rejoined (see the sanitizer).
        var captured = _registry.ReadCoupled(caller, pane.PaneId) ?? NothingCaptured;
        return _Serialize(new
        {
            ok = true,
            id = pane.PaneId,
            name = pane.Name,
            truncated = captured.Truncated,
            output = TerminalOutputSanitizer.ToPlainText(captured.Text),
        });
    }

    [McpServerTool(Name = "send_terminal")]
    [Description("Types input into a terminal pane the operator has open — you name it by the id or name from list_terminals. Set submit=true to press Enter after it (run the line). To interrupt a running command send the text \"\\u0003\" (Ctrl-C). Typing needs its own Approve from the operator, asked the first time you send to a pane — so if you were only reading it, expect one more prompt here. The operator watches live and can type alongside or Disconnect at any time. One agent at a time per pane. Use read_terminal to see the result.")]
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

        var caller = McpRequestContext.CurrentPaneId ?? session;
        if (await _EnsureCoupledAsync(caller, pane, TerminalCouplingMode.Drive).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        var bytes = Encoding.UTF8.GetBytes(submit ? input + "\r" : input);
        return _registry.SendInput(caller, pane.PaneId, bytes)
            ? _Serialize(new { ok = true, id = pane.PaneId, name = pane.Name, sentBytes = bytes.Length })
            : _Serialize(new { ok = false, error = "The terminal could not be written to — it may have closed or been disconnected." });
    }

    [McpServerTool(Name = "run_in_terminal")]
    [Description("Runs one command in a terminal pane the operator has open and waits for it to finish, returning its output and exit code — the wait-for-me version of send_terminal. Needs the same Approve as typing. It only works when the shell publishes shell-integration marks (OSC 133; fish 4+ has them, bash/zsh/PowerShell need the snippet their terminal ships) and when the shell is idle at a prompt: without a mark there is no honest way to tell a finished command from a slow one, and a full-screen program like vim or htop being open means the shell is not at a prompt. In either case this refuses and tells you so — use send_terminal plus read_terminal and judge for yourself.")]
    public async Task<string> RunInTerminal(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The terminal to run in, by its id or name from list_terminals, e.g. \"zsh-5\".")] string terminal,
        [Description("The command line to run. Sent as typed, then Enter.")] string command,
        [Description("How long to wait for it to finish, in seconds. Default 30, capped at 600.")] int timeoutSeconds = 30)
    {
        if (_registry.Resolve(terminal) is not { } pane)
        {
            return _Serialize(new { ok = false, error = "No such terminal pane — call list_terminals for the open panes and their ids." });
        }

        var caller = McpRequestContext.CurrentPaneId ?? session;
        if (await _EnsureCoupledAsync(caller, pane, TerminalCouplingMode.Drive).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        if (_registry.ShellStateOf(caller, pane.PaneId) is not { } before)
        {
            return _Serialize(new { ok = false, error = "Lost the connection to that terminal — it may have closed or the operator disconnected." });
        }

        // Refuse before typing anything, never after: a command typed into vim edits a file, and a command whose end
        // we cannot detect leaves the agent guessing at output that may still be arriving.
        if (!before.ShellIntegrationSeen)
        {
            return _Serialize(new
            {
                ok = false,
                error = "This shell does not publish shell-integration marks, so there is no way to tell when a command has finished. Nothing was run. Use send_terminal and then read_terminal, and judge from the output yourself.",
            });
        }

        if (!before.AtPrompt)
        {
            return _Serialize(new
            {
                ok = false,
                error = "That terminal is not sitting at a prompt — something is still running, or a full-screen program (an editor, a pager) has it. Nothing was run. Read it first, and use send_terminal if you mean to type into what is open.",
            });
        }

        var bytes = Encoding.UTF8.GetBytes(command + "\r");
        if (!_registry.SendInput(caller, pane.PaneId, bytes))
        {
            return _Serialize(new { ok = false, error = "The terminal could not be written to — it may have closed or been disconnected." });
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, MaxRunTimeoutSeconds));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollIntervalMs).ConfigureAwait(false);
            if (_registry.ShellStateOf(caller, pane.PaneId) is not { } now)
            {
                return _Serialize(new { ok = false, error = "The connection to that terminal ended while the command was running — the pane closed or the operator disconnected." });
            }

            // Both counters have to have moved: a finish on its own could belong to something that was already in
            // flight when we sent. It still cannot prove the finish is *ours* — the operator may type alongside, which
            // this design deliberately allows — but it rules out reporting a command we never started.
            if (now.CommandsFinished > before.CommandsFinished && now.CommandsStarted > before.CommandsStarted)
            {
                var captured = _registry.ReadCoupled(caller, pane.PaneId, before.CapturedSoFar) ?? NothingCaptured;
                return _Serialize(new
                {
                    ok = true,
                    id = pane.PaneId,
                    name = pane.Name,
                    exitCode = now.LastExitCode,
                    truncated = captured.Truncated,
                    output = TerminalOutputSanitizer.ToPlainText(captured.Text),
                });
            }
        }

        // Say plainly that it is still going: the operator can see it, and the agent can keep reading rather than
        // assume the command failed or was not sent.
        var partial = _registry.ReadCoupled(caller, pane.PaneId, before.CapturedSoFar) ?? NothingCaptured;
        return _Serialize(new
        {
            ok = false,
            error = $"The command is still running after {Math.Clamp(timeoutSeconds, 1, MaxRunTimeoutSeconds)}s. It was not cancelled — use read_terminal to keep watching, or send_terminal with \"\\u0003\" to interrupt it.",
            id = pane.PaneId,
            name = pane.Name,
            truncated = partial.Truncated,
            output = TerminalOutputSanitizer.ToPlainText(partial.Text),
        });
    }

    /// <summary>
    /// Ensures this session holds at least <paramref name="needed"/> on <paramref name="pane"/>, asking the operator
    /// once for exactly that much. Returns an error string to surface, or null when the session now holds it. An
    /// agent that has been watching and now wants to type gets a second prompt, worded as the widening it is — the
    /// operator's live view and Disconnect are the counterpart to whatever they granted.
    /// </summary>
    private async Task<string?> _EnsureCoupledAsync(string caller, TerminalPane pane, TerminalCouplingMode needed)
    {
        // Drive covers reading too; anything else has to be exactly what is needed. Spelled out rather than leaning on
        // the enum's order, so widening the enum later cannot silently widen the gate.
        var held = _registry.CouplingOf(caller, pane.PaneId);
        if (held == TerminalCouplingMode.Drive || held == needed)
        {
            return null;
        }

        if (held is null && _registry.IsCoupledByAnother(caller, pane.PaneId))
        {
            return $"Terminal pane \"{pane.Name}\" is already being used by another agent — only one agent at a time can use a pane.";
        }

        if (_consent is null)
        {
            return "Using a terminal pane needs the operator's approval, which is not available here.";
        }

        var decision = await _consent.RequestConsentAsync(_PromptFor(pane, needed, widening: held is not null)).ConfigureAwait(false);
        if (!decision.IsApproved)
        {
            return needed == TerminalCouplingMode.Watch
                ? "Reading that terminal was not approved by the operator."
                : "Typing into that terminal was not approved by the operator — you may still be able to read it.";
        }

        try
        {
            _registry.Couple(caller, pane.PaneId, needed);
        }
        catch (InvalidOperationException)
        {
            // The operator was deciding for as long as they took, and the world moved: another agent got the pane
            // first, or it closed. Surface it the way every other refusal here is surfaced, rather than letting an
            // exception out of a tool call.
            return $"Terminal pane \"{pane.Name}\" is no longer available — another agent took it, or it closed while the operator was deciding.";
        }

        return null;
    }

    // The prompt names the one thing being asked for, because that is what the operator is agreeing to. The pane name
    // goes in verbatim (it is the ground truth of which pane is taken over), folded to a single line first.
    private static ConsentRequest _PromptFor(TerminalPane pane, TerminalCouplingMode needed, bool widening) =>
        needed == TerminalCouplingMode.Watch
            ? new ConsentRequest(
                "An agent wants to read a terminal live",
                $"Let this agent read terminal pane {_SingleLine(pane.Name)}. It will see everything printed there from now on — not the earlier history. It cannot type into it: that is a separate question, asked separately. You can Disconnect at any time.",
                new ConsentSource(pane.PaneId, null, "Terminal MCP"),
                "terminal.watch",
                ConsentRisk.Dangerous)
            : new ConsentRequest(
                widening
                    ? "An agent that is reading a terminal now wants to type into it"
                    : "An agent wants to read and drive a terminal live",
                $"Let this agent type into terminal pane {_SingleLine(pane.Name)}, including Ctrl-C, and read everything printed there from now on — not the earlier history. You can watch, type alongside, and Disconnect at any time, which interrupts whatever it started.",
                new ConsentSource(pane.PaneId, null, "Terminal MCP"),
                "terminal.drive",
                ConsentRisk.Dangerous);

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
