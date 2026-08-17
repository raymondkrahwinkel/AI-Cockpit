namespace Cockpit.Core.Mcp;

// Applies the per-session MCP-server selection (#44) on top of the registry's own
// `McpServerConfig.Enabled`/`McpServerScope` filtering. The New-session dialog
// lets the operator uncheck individual registry servers for just that session; both consumers of the
// shared registry — `McpToolProvider` (local-model tool-loop) and `ClaudeCliProcess`'s
// `--mcp-config` fan-out — run the registry through this before their own filtering, so the
// per-session set is a pure narrowing, never a way to reach a server the registry itself disabled.
public static class McpServerRegistryFilter
{
    // Drops the *enabled* registry servers not named in `enabledServerNames`.
    // `null` means no session-level restriction was made (e.g. the New-session dialog
    // found no registry servers to offer), so the full registry passes through — save for the
    // `McpServerConfig.Internal` endpoints (AC-204), which never fan out to a session that did
    // not name them: they are the cockpit's own spawn-scoped endpoints (the Autopilot CEO/step tools), so an
    // unrelated session started with no selection while a run is live must not inherit them. An already
    // disabled entry always passes through untouched — the New-session checklist only ever offers
    // *enabled* registry servers, so a disabled one (including one that deliberately overrides and
    // suppresses a local-model built-in default of the same name, #26) was never part of what the
    // operator could check or uncheck, and must keep suppressing that default regardless of this filter.
    // An explicit selection that names an internal endpoint still mounts it — that is how a run's agents
    // reach their pane-scoped tools.
    //
    // `McpServerConfig.AlwaysMounted` endpoints pass through either way: they are the cockpit's own
    // plumbing rather than a choice, and they are hidden from the pickers precisely because unticking them is not
    // something the operator should be able to do by accident.
    public static IReadOnlyList<McpServerConfig> ApplySessionSelection(
        IReadOnlyList<McpServerConfig> registry,
        IReadOnlySet<string>? enabledServerNames) =>
        enabledServerNames is null
            ? [.. registry.Where(server => !server.Internal || server.AlwaysMounted)]
            : [.. registry.Where(server => server.AlwaysMounted || !server.Enabled || enabledServerNames.Contains(server.Name))];

    // The servers a picker may put in front of the operator: the enabled ones, minus the endpoints that are not a
    // choice at all. An `McpServerConfig.Internal` endpoint is the cockpit's own spawn-scoped tooling
    // and an `McpServerConfig.AlwaysMounted` one is mounted whatever is ticked, so offering either
    // invites an answer that changes nothing — or, worse, one that is silently overruled.
    //
    // One rule rather than the predicate written out per picker: the New-session checklist, the profile's
    // pre-selection, the project editor and the quick start all answer the same question, and four copies had
    // already begun to disagree about whether a disabled server counts.
    public static IReadOnlyList<McpServerConfig> OfferedToOperator(IReadOnlyList<McpServerConfig> registry) =>
        [.. registry.Where(server => server.Enabled && !server.Internal && !server.AlwaysMounted)];

    // The per-session selection a launch should actually apply: the explicit one it was handed, or — when it has
    // none — the profile's own saved selection (#44/AC-130). The New-session dialog computes a selection from the
    // profile's checklist, but a *programmatic* launch (a plugin/workflow shortcut, a restored session) has
    // no dialog, so it carries none; without this fallback such a launch would reach every enabled server
    // (a `null` pass-through) rather than the set the operator saved on the profile — the SDK/local
    // paths' half of the per-session-selection gap the TTY route already closed. An explicit session selection
    // (including a deliberate empty "these none") is honoured untouched; only a truly absent one falls back.
    public static IReadOnlySet<string>? EffectiveSessionSelection(
        IReadOnlySet<string>? sessionSelection,
        IReadOnlyList<string>? profileSelection) =>
        sessionSelection ?? (profileSelection is not null
            ? new HashSet<string>(profileSelection, StringComparer.OrdinalIgnoreCase)
            : null);

    // AC-869: folds auto-mount names (e.g. a git-repo-only Internal endpoint, judged outside this pure filter)
    // into `selection` before ApplySessionSelection narrows the registry. A null selection is materialized
    // into the no-selection fan-out (OfferedToOperator) plus those names — the same trick
    // AssistantSessionHost.McpSelection already uses for its own two servers. Empty input is a no-op.
    public static IReadOnlySet<string>? WithAutoMountedServers(
        IReadOnlySet<string>? selection,
        IReadOnlyList<McpServerConfig> registry,
        IReadOnlyCollection<string> autoMountedServerNames)
    {
        if (autoMountedServerNames.Count == 0)
        {
            return selection;
        }

        var result = selection is null
            ? new HashSet<string>(OfferedToOperator(registry).Select(server => server.Name), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(selection, StringComparer.OrdinalIgnoreCase);
        result.UnionWith(autoMountedServerNames);
        return result;
    }
}
