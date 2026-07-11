using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// One JSONL line's mapping result (#45 fase B1): the zero-or-more <see cref="PluginSessionEvent"/>s the line
/// produced, plus the session id the caller should carry into the <em>next</em> call. <see cref="SessionId"/>
/// only changes on a <c>thread.started</c> line (where Codex first reports its thread id) — every other line
/// echoes back whatever <see cref="SessionId"/> <see cref="CodexJsonlEventMapper.ParseLine"/> was called with,
/// so <see cref="CliSubprocessPluginSessionDriver"/> can track the Codex thread id across turns for
/// <c>codex exec resume &lt;id&gt;</c> without the mapper itself needing to be stateful.
/// </summary>
internal sealed record CodexJsonlMapResult(IReadOnlyList<PluginSessionEvent> Events, string? SessionId);
