using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.Browser;

internal sealed class DepotFailure(string status, string message) : Exception(message)
{
    public string Status { get; } = status;
}

internal sealed record DepotProjectRow(string Argument, string Name, DepotProjectRole Role);

// AC-1574: the Depot calls the file browser and the publisher share, through the connection's own MCP server so the host
// holds the token. A failure comes out as a DepotFailure; nothing here logs.
internal sealed class DepotMcp(ICockpitHost host, Func<IReadOnlyList<DepotConnectionRegistration>> connections)
{
    public async Task<string> CallAsync(
        string connectionId, string tool, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken)
    {
        var connection = connections().FirstOrDefault(candidate => candidate.Id == connectionId)
            ?? throw new DepotFailure(DepotBrowserStatus.Error, "That Depot connection is no longer configured.");
        var result = await host.CallMcpToolAsync(connection.McpServerName, tool, arguments, projectId: null, cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            PluginMcpToolCallOutcome.Success => result.Content ?? string.Empty,
            PluginMcpToolCallOutcome.AuthorizationRequired => throw new DepotFailure(DepotBrowserStatus.SignIn, $"Sign in to {connection.Name}"),
            _ => throw new DepotFailure(DepotBrowserStatus.Error, result.Error is { Length: > 0 } error ? error : "Depot did not answer."),
        };
    }

    public async Task<IReadOnlyList<DepotProjectRow>> ListProjectsAsync(string connectionId, CancellationToken cancellationToken)
    {
        using var document = Parse(await CallAsync(connectionId, "list_projects", null, cancellationToken).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. projects.EnumerateArray()
            .Select(project => (Slug: String(project, "slug"), Address: String(project, "address"), Project: project))
            .Where(row => row.Slug is not null)
            .Select(row => new DepotProjectRow(
                row.Address ?? row.Slug!,
                String(row.Project, "name") ?? row.Slug!,
                DepotProjectRoleParser.Parse(String(row.Project, "role"))))];
    }

    public async Task<DepotProjectRole> RoleAsync(string connectionId, string project, CancellationToken cancellationToken) =>
        (await ListProjectsAsync(connectionId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(row => row.Argument == project)?.Role ?? DepotProjectRole.Unknown;

    public async Task RequireEditorAsync(string connectionId, string project, string action, CancellationToken cancellationToken)
    {
        if (!(await RoleAsync(connectionId, project, cancellationToken).ConfigureAwait(false)).CanWrite())
        {
            throw new DepotFailure(DepotBrowserStatus.Error, $"{action} needs the Editor role in this project.");
        }
    }

    public static async Task<DepotBrowserAnswer<T>> RunAsync<T>(Func<Task<T>> body)
    {
        try
        {
            return DepotBrowserAnswer<T>.Success(await body().ConfigureAwait(false));
        }
        catch (DepotFailure failure)
        {
            return DepotBrowserAnswer<T>.Failure(failure.Status, failure.Message);
        }
    }

    public static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new DepotFailure(DepotBrowserStatus.Error, $"The request names no {name}.") : value;

    public static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "Depot's answer came back in an unexpected shape.");
        }
    }

    public static IEnumerable<JsonElement> Entries(JsonElement root) =>
        root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array ? entries.EnumerateArray() : [];

    public static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
