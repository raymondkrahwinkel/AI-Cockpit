using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

// One JSONL line's mapping result (#45 fase B1): the zero-or-more `PluginSessionEvent`s the line
// produced, plus the session id the caller should carry into the *next* call. `SessionId`
// only changes on a `thread.started` line (where Codex first reports its thread id) — every other line
// echoes back whatever `SessionId` `CodexJsonlEventMapper.ParseLine` was called with,
// so `CliSubprocessPluginSessionDriver` can track the Codex thread id across turns for
// `codex exec resume &lt;id&gt;` without the mapper itself needing to be stateful.
internal sealed record CodexJsonlMapResult(IReadOnlyList<PluginSessionEvent> Events, string? SessionId);
