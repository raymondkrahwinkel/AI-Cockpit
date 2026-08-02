using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

// The `codex` CLI as a TTY provider (#45 fase B2): hosts the real interactive Codex TUI in a cockpit
// pane, under the same `cli-agent-provider.codex` provider id the headless driver
// (`CliSubprocessPluginSessionDriver`) registers.
// Deliberately not built from the headless `CliAgentConfig.SubCommand`/`CliAgentConfig.EffectiveOutputFormatArgs`
// path: `codex exec --json` is the headless, single-turn mode
// `CliSubprocessPluginSessionDriver.BuildArguments` builds; the TTY spawn instead runs bare
// `codex` (a fresh session) or `codex resume [SESSION_ID|--last]` (an earlier one) with no
// subcommand and no `--json`, which is what actually launches Codex's interactive TUI — confirmed
// against the real CLI's own `--help`/`resume --help` output, not assumed from Claude's shape:
// - Codex has no `exec`/`--json` equivalent for the TUI — those flags are exec-only, and
// passing them here would launch the headless mode instead of the TUI.
// - Codex has no `--effort` flag at all (Claude's reasoning-effort knob has no Codex analogue),
// so this provider declares no such option — a control for a knob the CLI does not have would be dead
// UI, not a start default.
// - Resume is positional (`codex resume &lt;SESSION_ID&gt;`) or `--last`, not Claude's
// `--resume &lt;id&gt;`/`--continue` pair.
// - Codex's approval-vs-sandbox split (`--ask-for-approval` is a separate, real flag from
// `--sandbox`) has no field on `CliAgentConfig` to carry a per-profile default, so only
// sandbox — the one Codex knob the config already models — is wired through here; see the plugin's design
// doc for approval-policy as a follow-up rather than something invented in this pass.
internal sealed class CodexTtyProvider(Func<string, string?>? managedResolver = null) : IPluginTtyProvider
{
    // The option key the New-session dialog stores Codex's chosen sandbox policy under — Codex's own word
    // for the knob (`--sandbox &lt;read-only|workspace-write|danger-full-access&gt;`), not Claude's
    // `permission-mode`: see `TtyLaunchOption`'s own remark that a provider speaks its own
    // vocabulary rather than pretending to be Claude.
    public const string SandboxOptionKey = "sandbox";

    // The option key for Codex's `-m/--model &lt;MODEL&gt;` — the one launch-only knob Codex and Claude happen to name the same.
    public const string ModelOptionKey = "model";

    public PluginTtyLaunchSpec BuildLaunch(PluginTtyLaunchContext context)
    {
        // Codex writes its own thread state under ~/.codex/sessions, but reading a thread id back out of it
        // reliably has not been investigated for AC-408 — guessing at an on-disk format and being wrong is worse
        // than an honest Unsupported, so this TTY route reports that until the format is actually confirmed.
        context.ReportConversationId?.Invoke(PluginConversationId.Unsupported);

        var config = _DeserializeConfig(context.ConfigJson);
        // A cockpit-managed install (AC-20), if present, is preferred over PATH.
        var executablePath = CliExecutableLocator.Resolve(string.IsNullOrWhiteSpace(config.Command) ? "codex" : config.Command, managedResolver);

        // The session's Cockpit MCP servers (#26/AC-77) fan into the interactive TUI the same way the headless
        // app-server spawn takes them (CodexAppServerSessionDriver): as `-c mcp_servers.<name>={…}` overrides
        // built by CodexMcpConfig, with any bearer token riding the process environment rather than the command
        // line. Claude's TTY provider does the equivalent via --mcp-config; without this the Codex TUI only ever
        // sees its own ~/.codex servers, never the cockpit's. Empty when the session resolved no servers.
        var mcpLaunch = CodexMcpConfig.Build(context.McpServers);

        return new PluginTtyLaunchSpec(
            executablePath,
            BuildArguments(config, context.Options, context.Resume, mcpLaunch.ConfigArgs),
            BuildEnvironmentOverlay(config, mcpLaunch.EnvironmentVariables),
            context.WorkingDirectory,
            SessionScopedFiles: []);
    }

    private static CliAgentConfig _DeserializeConfig(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return new CliAgentConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<CliAgentConfig>(configJson, CliAgentConfig.JsonOptions) ?? new CliAgentConfig();
        }
        catch (JsonException)
        {
            return new CliAgentConfig();
        }
    }

    // Builds the interactive-TUI command line: no `exec`, no `--json` — see the type-level remark
    // for why. `internal` (and free of any pty/process dependency) so the resume-vs-fresh and
    // option-vs-config-default branching is unit-testable without spawning a real CLI.
    internal static List<string> BuildArguments(CliAgentConfig config, IReadOnlyDictionary<string, string> options, PluginTtyResume? resume, IReadOnlyList<string>? mcpConfigArgs = null)
    {
        var arguments = new List<string>();

        // The MCP `-c mcp_servers.*` overrides (AC-77) must precede any `resume` subcommand: Codex reads `-c` as a
        // global config flag, and it takes those before the subcommand — the same placement the app-server spawn
        // uses (`[.. configArgs, "app-server"]`). Prepending them here keeps resume + MCP working together.
        if (mcpConfigArgs is { Count: > 0 })
        {
            arguments.AddRange(mcpConfigArgs);
        }

        if (resume is not null)
        {
            arguments.Add("resume");
            if (!string.IsNullOrWhiteSpace(resume.SessionId))
            {
                arguments.Add(resume.SessionId.Trim());
            }
            else
            {
                arguments.Add("--last");
            }
        }

        var sandbox = CliAgentConfig.ResolveOption(options, SandboxOptionKey, config.SandboxMode);
        if (!string.IsNullOrWhiteSpace(sandbox))
        {
            arguments.Add("--sandbox");
            arguments.Add(sandbox);
        }

        var model = CliAgentConfig.ResolveOption(options, ModelOptionKey, config.Model);
        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        return arguments;
    }

    // `CODEX_HOME` plus any per-server MCP bearer-token env vars (AC-77). The API key still goes nowhere near
    // a TTY spawn's overlay: the interactive TUI prompts for `codex login` itself, same as Claude's TTY mode
    // never carries an API key. A cockpit-hosted server's `COCKPIT_MCP_KEY` is not set here either — it is
    // host-controlled and already on the base environment from `TtyLauncher` (AC-40); only the non-hosted
    // `COCKPIT_MCP_TOKEN_*` vars this session minted travel through the overlay.
    internal static IReadOnlyDictionary<string, string?> BuildEnvironmentOverlay(CliAgentConfig config, IReadOnlyDictionary<string, string?>? mcpEnvironment = null)
    {
        var overlay = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(config.ConfigDir))
        {
            overlay["CODEX_HOME"] = config.ConfigDir;
        }

        if (mcpEnvironment is not null)
        {
            foreach (var (key, value) in mcpEnvironment)
            {
                overlay[key] = value;
            }
        }

        return overlay;
    }
}
