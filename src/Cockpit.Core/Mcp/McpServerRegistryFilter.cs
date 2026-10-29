namespace Cockpit.Core.Mcp;

// #44: applies the per-session MCP-server selection on top of the registry's own Enabled/Scope filtering. All three
// registry consumers (`McpToolProvider`, `PluginSessionDriverAdapter`, `PluginTtySessionProviderAdapter`) run through
// this first, so the per-session set is a pure narrowing, never a way to reach a server the registry itself disabled.
public static class McpServerRegistryFilter
{
    // AC-204: drops enabled registry servers not named in `enabledServerNames`. `null` means no restriction;
    // `Internal` endpoints never fan out unnamed, and `AlwaysMounted` ones always pass through as cockpit
    // plumbing rather than an operator choice.
    public static IReadOnlyList<McpServerConfig> ApplySessionSelection(
        IReadOnlyList<McpServerConfig> registry,
        IReadOnlySet<string>? enabledServerNames) =>
        enabledServerNames is null
            ? [.. registry.Where(server => !server.Internal || server.AlwaysMounted)]
            : [.. registry.Where(server => server.AlwaysMounted || !server.Enabled || enabledServerNames.Contains(server.Name))];

    // The servers a picker may offer: enabled ones minus `Internal`/`AlwaysMounted` endpoints, which are not a
    // real choice (ticking either changes nothing, or is silently overruled). One rule instead of four pickers
    // (checklist, profile pre-selection, project editor, quick start) each answering it differently.
    public static IReadOnlyList<McpServerConfig> OfferedToOperator(IReadOnlyList<McpServerConfig> registry) =>
        [.. registry.Where(server => server.Enabled && !server.Internal && !server.AlwaysMounted)];

    // #44/AC-130: the per-session selection to apply — the explicit one it was handed, or the profile's saved
    // selection when there is none. A programmatic launch (plugin shortcut, restored session) has no New-session
    // dialog to compute one, so without this fallback it would reach every enabled server instead of the profile's.
    public static IReadOnlySet<string>? EffectiveSessionSelection(
        IReadOnlySet<string>? sessionSelection,
        IReadOnlyList<string>? profileSelection) =>
        sessionSelection ?? (profileSelection is not null
            ? new HashSet<string>(profileSelection, StringComparer.OrdinalIgnoreCase)
            : null);

    // A launcher checklist deliberately hides internal endpoints, but an explicitly configured management profile
    // must not lose one merely because the operator could not see a row for it. Only internal names already present
    // in the profile are carried; visible names remain governed by the launcher's checkboxes.
    public static IReadOnlySet<string>? WithInternalProfileServers(
        IReadOnlySet<string>? sessionSelection,
        IReadOnlySet<string>? profileSelection,
        IReadOnlyList<McpServerConfig> registry)
    {
        if (profileSelection is null)
        {
            return sessionSelection;
        }

        var internalNames = registry
            .Where(server => server.Internal && profileSelection.Contains(server.Name))
            .Select(server => server.Name)
            .ToList();
        if (internalNames.Count == 0)
        {
            return sessionSelection;
        }

        var result = sessionSelection is null
            ? new HashSet<string>(OfferedToOperator(registry).Select(server => server.Name), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(sessionSelection, StringComparer.OrdinalIgnoreCase);
        result.UnionWith(internalNames);
        return result;
    }

    // AC-869: folds auto-mount names into `selection` before ApplySessionSelection narrows the registry. A
    // null selection is materialized into the no-selection fan-out (OfferedToOperator) plus those names —
    // the same trick AssistantSessionHost.McpSelection uses for its own two servers.
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
