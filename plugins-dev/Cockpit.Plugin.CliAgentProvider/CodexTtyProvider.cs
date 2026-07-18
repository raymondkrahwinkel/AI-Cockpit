using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// The <c>codex</c> CLI as a TTY provider (#45 fase B2): hosts the real interactive Codex TUI in a cockpit
/// pane, under the same <c>cli-agent-provider.codex</c> provider id the headless driver
/// (<see cref="CliSubprocessPluginSessionDriver"/>) registers.
/// </summary>
/// <remarks>
/// Deliberately not built from the headless <see cref="CliAgentConfig.SubCommand"/>/<see cref="CliAgentConfig.EffectiveOutputFormatArgs"/>
/// path: <c>codex exec --json</c> is the headless, single-turn mode
/// <see cref="CliSubprocessPluginSessionDriver.BuildArguments"/> builds; the TTY spawn instead runs bare
/// <c>codex</c> (a fresh session) or <c>codex resume [SESSION_ID|--last]</c> (an earlier one) with no
/// subcommand and no <c>--json</c>, which is what actually launches Codex's interactive TUI — confirmed
/// against the real CLI's own <c>--help</c>/<c>resume --help</c> output, not assumed from Claude's shape:
/// <list type="bullet">
/// <item>Codex has no <c>exec</c>/<c>--json</c> equivalent for the TUI — those flags are exec-only, and
/// passing them here would launch the headless mode instead of the TUI.</item>
/// <item>Codex has no <c>--effort</c> flag at all (Claude's reasoning-effort knob has no Codex analogue),
/// so this provider declares no such option — a control for a knob the CLI does not have would be dead
/// UI, not a start default.</item>
/// <item>Resume is positional (<c>codex resume &lt;SESSION_ID&gt;</c>) or <c>--last</c>, not Claude's
/// <c>--resume &lt;id&gt;</c>/<c>--continue</c> pair.</item>
/// <item>Codex's approval-vs-sandbox split (<c>--ask-for-approval</c> is a separate, real flag from
/// <c>--sandbox</c>) has no field on <see cref="CliAgentConfig"/> to carry a per-profile default, so only
/// sandbox — the one Codex knob the config already models — is wired through here; see the plugin's design
/// doc for approval-policy as a follow-up rather than something invented in this pass.</item>
/// </list>
/// </remarks>
internal sealed class CodexTtyProvider(Func<string, string?>? managedResolver = null) : IPluginTtyProvider
{
    /// <summary>
    /// The option key the New-session dialog stores Codex's chosen sandbox policy under — Codex's own word
    /// for the knob (<c>--sandbox &lt;read-only|workspace-write|danger-full-access&gt;</c>), not Claude's
    /// <c>permission-mode</c>: see <c>TtyLaunchOption</c>'s own remark that a provider speaks its own
    /// vocabulary rather than pretending to be Claude.
    /// </summary>
    public const string SandboxOptionKey = "sandbox";

    /// <summary>The option key for Codex's <c>-m/--model &lt;MODEL&gt;</c> — the one launch-only knob Codex and Claude happen to name the same.</summary>
    public const string ModelOptionKey = "model";

    public PluginTtyLaunchSpec BuildLaunch(PluginTtyLaunchContext context)
    {
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

    /// <summary>
    /// Builds the interactive-TUI command line: no <c>exec</c>, no <c>--json</c> — see the type-level remark
    /// for why. <c>internal</c> (and free of any pty/process dependency) so the resume-vs-fresh and
    /// option-vs-config-default branching is unit-testable without spawning a real CLI.
    /// </summary>
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

    /// <summary>
    /// <c>CODEX_HOME</c> plus any per-server MCP bearer-token env vars (AC-77). The API key still goes nowhere near
    /// a TTY spawn's overlay: the interactive TUI prompts for <c>codex login</c> itself, same as Claude's TTY mode
    /// never carries an API key. A cockpit-hosted server's <c>COCKPIT_MCP_KEY</c> is not set here either — it is
    /// host-controlled and already on the base environment from <c>TtyLauncher</c> (AC-40); only the non-hosted
    /// <c>COCKPIT_MCP_TOKEN_*</c> vars this session minted travel through the overlay.
    /// </summary>
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
