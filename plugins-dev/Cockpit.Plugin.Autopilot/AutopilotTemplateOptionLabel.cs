namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The origin suffix a template carries in the run-picker (AC-189, slice 3). Two trackers can both register a "Bug fix"
/// and a "Feature" template, so the name alone is ambiguous; appending where each came from tells them apart —
/// <c>Feature · YouTrack</c> vs <c>Feature · GitHub Issues</c>, <c>Bug fix · Yours</c>, <c>Bug fix · Built-in</c>. For a
/// plugin-contributed template the suffix is the contributing plugin's readable name, resolved from its
/// <see cref="AutopilotTemplate.OwnerPluginId"/> through a lookup (the host's installed plugins); when that yields
/// nothing — an unknown or absent owner — it falls back to the bare id so the origin is never blank. Pure so the
/// name → "name · origin" rule is unit-testable without a host or a UI.
/// </summary>
internal static class AutopilotTemplateOptionLabel
{
    /// <summary>The origin suffix alone: "Built-in", "Yours", or a plugin's name (its id when the name is unknown).</summary>
    public static string OriginLabel(AutopilotTemplate template, Func<string, string?> pluginName) => template.Origin switch
    {
        AutopilotTemplateOrigin.Builtin => "Built-in",
        AutopilotTemplateOrigin.User => "Yours",
        _ => template.OwnerPluginId is { Length: > 0 } id
            ? pluginName(id) is { Length: > 0 } name ? name : id
            : "Plugin",
    };

    /// <summary>The full picker option label: the template name with its origin appended, e.g. <c>Feature · YouTrack</c>.</summary>
    public static string For(AutopilotTemplate template, Func<string, string?> pluginName) =>
        $"{template.Name} · {OriginLabel(template, pluginName)}";
}
