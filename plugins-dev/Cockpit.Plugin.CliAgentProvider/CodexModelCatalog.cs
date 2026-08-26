using System.Text.Json;

namespace Cockpit.Plugin.CliAgentProvider;

// Reads the models a logged-in Codex offers, by spawning a one-shot `codex app-server`, doing the
// initialize handshake and calling the `model/list` JSON-RPC method (increment 2 step C). It fills the
// New-session dialog's Model choices with the real, current models instead of free text. No `thread/start`
// is issued, so listing costs no credits. Best-effort by contract: the caller treats any failure (codex
// missing, not logged in, slow) as "no dynamic models — keep the free-text field".
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

        return ParseListing(result);
    }

    private static string _WorkingDirectory(CliAgentConfig config) =>
        string.IsNullOrWhiteSpace(config.WorkingDirectory) ? Environment.CurrentDirectory : config.WorkingDirectory;

    // Parses a `model/list` reply into the ids it offers and the default, so the live-control path (#45 D4) can reuse it against its own already-running app-server connection instead of spawning a second one.
    internal static CodexModelListing ParseListing(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return CodexModelListing.Empty;
        }

        var ids = new List<string>();
        string? defaultId = null;
        var reasoningEffortsById = new Dictionary<string, IReadOnlyList<string>>();
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

            reasoningEffortsById[id] = _ParseReasoningEfforts(entry);
        }

        return new CodexModelListing(ids, defaultId, reasoningEffortsById);
    }

    // `supportedReasoningEfforts` (AC-1101): each model reports its own reasoning-effort presets, not a fixed set
    // shared by all — sol/terra offer "ultra", luna and 5.5 do not. Reading it per model, instead of a hardcoded
    // list, is what lets the effort control filter to what the selected model actually supports.
    private static IReadOnlyList<string> _ParseReasoningEfforts(JsonElement entry)
    {
        if (!entry.TryGetProperty("supportedReasoningEfforts", out var efforts) || efforts.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var preset in efforts.EnumerateArray())
        {
            if (_StringProperty(preset, "reasoningEffort") is { Length: > 0 } effort)
            {
                values.Add(effort);
            }
        }

        return values;
    }

    private static string? _StringProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}

// The models Codex reported, which one it marks default, and each model's own reasoning-effort presets (keyed by
// model id) — empty when the listing could not be read.
internal sealed record CodexModelListing(IReadOnlyList<string> Ids, string? DefaultId, IReadOnlyDictionary<string, IReadOnlyList<string>>? ReasoningEffortsById = null)
{
    public static CodexModelListing Empty { get; } = new([], null);

    // The reasoning-effort presets the given model reports, or empty when the listing carries none for it (an
    // unlisted model, a Codex build too old to report the field) — the caller treats that the same as "no live
    // effort control" rather than falling back to a guessed set.
    public IReadOnlyList<string> ReasoningEffortsFor(string? modelId) =>
        modelId is { Length: > 0 } id && ReasoningEffortsById?.TryGetValue(id, out var efforts) is true ? efforts : [];
}
