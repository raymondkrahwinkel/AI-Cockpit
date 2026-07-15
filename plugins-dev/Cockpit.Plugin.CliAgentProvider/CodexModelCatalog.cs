using System.Text.Json;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// Reads the models a logged-in Codex offers, by spawning a one-shot <c>codex app-server</c>, doing the
/// initialize handshake and calling the <c>model/list</c> JSON-RPC method (increment 2 step C). It fills the
/// New-session dialog's Model choices with the real, current models instead of free text. No <c>thread/start</c>
/// is issued, so listing costs no credits. Best-effort by contract: the caller treats any failure (codex
/// missing, not logged in, slow) as "no dynamic models — keep the free-text field".
/// </summary>
internal static class CodexModelCatalog
{
    private const string _ClientName = "cockpit";
    private const string _ClientVersion = "1.0.0";

    public static async Task<CodexModelListing> ListAsync(
        Func<ICliSubprocess> subprocessFactory,
        CliAgentConfig config,
        string executablePath,
        CancellationToken cancellationToken)
    {
        await using var connection = new CodexAppServerConnection(subprocessFactory());
        connection.Start(executablePath, _WorkingDirectory(config), config.BuildEnvironmentVariables());

        await connection.SendRequestAsync("initialize", new { clientInfo = new { name = _ClientName, version = _ClientVersion } }, cancellationToken).ConfigureAwait(false);
        await connection.SendNotificationAsync("initialized", null, cancellationToken).ConfigureAwait(false);
        var result = await connection.SendRequestAsync("model/list", new { }, cancellationToken).ConfigureAwait(false);

        return _Parse(result);
    }

    private static string _WorkingDirectory(CliAgentConfig config) =>
        string.IsNullOrWhiteSpace(config.WorkingDirectory) ? Environment.CurrentDirectory : config.WorkingDirectory;

    private static CodexModelListing _Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return CodexModelListing.Empty;
        }

        var ids = new List<string>();
        string? defaultId = null;
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || (entry.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True))
            {
                continue;
            }

            var id = _StringProperty(entry, "id") ?? _StringProperty(entry, "model");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            ids.Add(id);
            if (defaultId is null && entry.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True)
            {
                defaultId = id;
            }
        }

        return new CodexModelListing(ids, defaultId);
    }

    private static string? _StringProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}

/// <summary>The models Codex reported, and which one it marks default — empty when the listing could not be read.</summary>
internal sealed record CodexModelListing(IReadOnlyList<string> Ids, string? DefaultId)
{
    public static CodexModelListing Empty { get; } = new([], null);
}
