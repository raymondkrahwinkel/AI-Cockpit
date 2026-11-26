using System.Text.Json;
using Cockpit.Plugins.Abstractions.Profiles;

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
        CancellationToken cancellationToken,
        bool readAllPages = false)
    {
        await using var connection = new CodexAppServerConnection(subprocessFactory());
        connection.Start(executablePath, _WorkingDirectory(config), config.BuildEnvironmentVariables());

        await connection.SendRequestAsync("initialize", new { clientInfo = new { name = _ClientName, version = _ClientVersion } }, cancellationToken).ConfigureAwait(false);
        await connection.SendNotificationAsync("initialized", null, cancellationToken).ConfigureAwait(false);
        var result = await connection.SendRequestAsync("model/list", new { }, cancellationToken).ConfigureAwait(false);

        var listing = ParseListing(result);
        var cursor = readAllPages && listing.IsKnown ? _StringProperty(result, "nextCursor") : null;
        if (string.IsNullOrEmpty(cursor))
        {
            return listing;
        }

        var ids = listing.Ids.ToList();
        var defaultId = listing.DefaultId;
        var efforts = listing.ReasoningEffortsById?.ToDictionary() ?? new Dictionary<string, IReadOnlyList<string>>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        while (cursor is { Length: > 0 })
        {
            if (!seenCursors.Add(cursor))
            {
                return CodexModelListing.Empty;
            }

            result = await connection.SendRequestAsync("model/list", new { cursor }, cancellationToken).ConfigureAwait(false);
            var page = ParseListing(result);
            if (!page.IsKnown)
            {
                return CodexModelListing.Empty;
            }

            ids.AddRange(page.Ids);
            defaultId ??= page.DefaultId;
            foreach (var id in page.Ids)
            {
                if (page.ReasoningEffortsById?.TryGetValue(id, out var values) is true)
                {
                    efforts[id] = values;
                }
            }

            cursor = _StringProperty(result, "nextCursor");
        }

        return new(ids, defaultId, efforts);
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
        var isKnown = true;
        var reasoningEffortsById = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                isKnown = false;
                continue;
            }

            if (entry.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            var id = _StringProperty(entry, "id") ?? _StringProperty(entry, "model");
            if (string.IsNullOrWhiteSpace(id))
            {
                isKnown = false;
                continue;
            }

            ids.Add(id);
            if (defaultId is null && entry.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True)
            {
                defaultId = id;
            }

            if (_ParseReasoningEfforts(entry) is { } efforts)
            {
                reasoningEffortsById[id] = efforts;
            }
        }

        return new CodexModelListing(ids, defaultId, reasoningEffortsById) { IsKnown = isKnown };
    }

    // `supportedReasoningEfforts` (AC-1101): each model reports its own reasoning-effort presets, not a fixed set
    // shared by all — sol/terra offer "ultra", luna and 5.5 do not. Reading it per model, instead of a hardcoded
    // list, is what lets the effort control filter to what the selected model actually supports.
    private static IReadOnlyList<string>? _ParseReasoningEfforts(JsonElement entry)
    {
        if (!entry.TryGetProperty("supportedReasoningEfforts", out var efforts) || efforts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<string>();
        foreach (var preset in efforts.EnumerateArray())
        {
            if (preset.ValueKind == JsonValueKind.Object && _StringProperty(preset, "reasoningEffort") is { Length: > 0 } effort)
            {
                values.Add(effort);
            }
        }

        return values.Count > 0 || efforts.GetArrayLength() == 0 ? values : null;
    }

    private static string? _StringProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}

// The models Codex reported, which one it marks default, and each model's own reasoning-effort presets (keyed by
// model id) — empty when the listing could not be read.
internal sealed record CodexModelListing(IReadOnlyList<string> Ids, string? DefaultId, IReadOnlyDictionary<string, IReadOnlyList<string>>? ReasoningEffortsById = null)
{
    public static CodexModelListing Empty { get; } = new([], null) { IsKnown = false };

    public bool IsKnown { get; init; } = true;

    public PluginProfileModelCatalog ToProfileCatalog() =>
        !IsKnown ? PluginProfileModelCatalog.Unknown : new(
            PluginCatalogStatus.Known,
            Ids.Distinct(StringComparer.Ordinal).Select(id =>
                ReasoningEffortsById?.TryGetValue(id, out var efforts) is true
                    ? new PluginProfileModel(id, PluginCatalogStatus.Known, efforts)
                    : new PluginProfileModel(id, PluginCatalogStatus.Unknown, [])).ToArray());

    // The reasoning-effort presets the given model reports, or empty when the listing carries none for it (an
    // unlisted model, a Codex build too old to report the field) — the caller treats that the same as "no live
    // effort control" rather than falling back to a guessed set.
    public IReadOnlyList<string> ReasoningEffortsFor(string? modelId) =>
        modelId is { Length: > 0 } id && ReasoningEffortsById?.TryGetValue(id, out var efforts) is true ? efforts : [];
}
