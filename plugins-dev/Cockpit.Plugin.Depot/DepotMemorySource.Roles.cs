using System.Text.Json;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Depot;

internal static partial class DepotMemorySource
{
    private static async Task<RoleListResult> _ListRolesAsync(
        ICockpitHost host, DepotConnectionRegistration connection, string value, CancellationToken cancellationToken)
    {
        if (_BrainInstruction(connection, value) is null)
        {
            return new(RoleSourceOutcome.NoRoles, []);
        }
        var result = await host.CallMcpToolAsync(connection.McpServerName, "list_agents",
            new Dictionary<string, object?> { ["project"] = value[..value.LastIndexOf('@')] },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Outcome != PluginMcpToolCallOutcome.Success || result.Content is null)
        {
            return new(result.Outcome == PluginMcpToolCallOutcome.Unavailable ? RoleSourceOutcome.NoRoles : RoleSourceOutcome.Unavailable, []);
        }
        try
        {
            using var document = JsonDocument.Parse(result.Content);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("agents", out var agents) || agents.ValueKind != JsonValueKind.Array ||
                agents.EnumerateArray().Any(agent => agent.ValueKind != JsonValueKind.Object))
            {
                return new(RoleSourceOutcome.NoRoles, []);
            }
            var roles = agents.EnumerateArray().Take(64)
                .Select(agent => new SessionRole(_RoleString(agent, "name") ?? string.Empty, _RoleString(agent, "description")))
                .Where(role => !string.IsNullOrWhiteSpace(role.Name) && !string.Equals(role.Name, "none", StringComparison.OrdinalIgnoreCase))
                .DistinctBy(role => role.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (roles.Length == 0)
            {
                return new(RoleSourceOutcome.NoRoles, []);
            }
            // A partial server upgrade must not offer roles it cannot load (DEP-610).
            var probe = await _LoadRoleAsync(host, connection, value, roles[0].Name, cancellationToken).ConfigureAwait(false);
            return probe.Outcome == RoleSourceOutcome.Success
                ? new(RoleSourceOutcome.Success, roles)
                : new(RoleSourceOutcome.NoRoles, []);
        }
        catch (JsonException)
        {
            return new(RoleSourceOutcome.Unavailable, []);
        }
    }

    private static async Task<RoleLoadResult> _LoadRoleAsync(
        ICockpitHost host, DepotConnectionRegistration connection, string value, string role, CancellationToken cancellationToken)
    {
        if (_BrainInstruction(connection, value) is null || string.Equals(role, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new(RoleSourceOutcome.NoRoles);
        }
        var result = await host.CallMcpToolAsync(connection.McpServerName, "get_agent",
            new Dictionary<string, object?> { ["project"] = value[..value.LastIndexOf('@')], ["agent"] = role },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Outcome != PluginMcpToolCallOutcome.Success || result.Content is null)
        {
            return new(result.Outcome == PluginMcpToolCallOutcome.Unavailable ? RoleSourceOutcome.NoRoles : RoleSourceOutcome.Unavailable);
        }
        try
        {
            using var document = JsonDocument.Parse(result.Content);
            var root = document.RootElement;
            var instructions = _RoleString(root, "instructions");
            if (string.IsNullOrWhiteSpace(instructions) || !string.Equals(_RoleString(root, "name"), role, StringComparison.OrdinalIgnoreCase))
            {
                return new(RoleSourceOutcome.NoRoles);
            }
            var text = instructions;
            if (root.TryGetProperty("skills", out var skills) && skills.ValueKind == JsonValueKind.Array)
            {
                foreach (var skill in skills.EnumerateArray())
                {
                    if (_RoleString(skill, "name") is { Length: > 0 } name)
                    {
                        text += $"\n- Skill: {name}" + (_RoleString(skill, "description") is { Length: > 0 } description ? $": {description}" : string.Empty);
                    }
                    if (text.Length >= 12000)
                    {
                        break;
                    }
                }
            }
            return new(RoleSourceOutcome.Success, text[..Math.Min(text.Length, 12000)]);
        }
        catch (JsonException)
        {
            return new(RoleSourceOutcome.Unavailable);
        }
    }

    private static string? _RoleString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
