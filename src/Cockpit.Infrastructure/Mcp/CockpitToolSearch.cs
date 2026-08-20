using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Cockpit.Infrastructure.Mcp;

// `cockpit-tools` (AC-963): the two tools a session offers instead of its whole catalogue — `search_tools` to find
// one by name or description, `call_tool` to run it. The point is arithmetic: a session with ~110 mounted tools
// spends 25-40k tokens on schemas in *every* request before a word of conversation, which a self-hosted model with
// a small context window cannot afford. Two fixed schemas cost the same whether one server is mounted or twenty.
//
// `call_tool` runs the caller's own already-gated `AIFunction` — the same instance a direct call would reach — so
// the approval flow, the tool classes and the AC-79 delegation ceiling apply to the real tool by its real name.
// Reaching past that (to the unwrapped tool, or to `IMcpToolInvoker`, which connects outside the session and knows
// no gate) would make this the back door a delegated session escapes its permission ceiling through.
internal static class CockpitToolSearch
{
    // Up to this many tools a session keeps everything preloaded, exactly as it did before this existed; above it
    // the catalogue moves behind `search_tools`. A constant rather than a profile setting: the number that matters
    // is the token cost of the schemas, which the operator cannot see and should not have to tune.
    public const int PreloadThreshold = 30;

    public const string SearchToolName = "search_tools";
    public const string CallToolName = "call_tool";

    private const int DefaultLimit = 10;
    private const int MaxLimit = 50;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    // The two proxy tools over `catalog` — the session's gated tools, origin included.
    public static IReadOnlyList<AITool> Build(IReadOnlyList<McpSessionTool> catalog) =>
    [
        AIFunctionFactory.Create(
            ([Description("What the tool should do or be called, in words — e.g. \"set status\", \"youtrack comment\", \"read file\". Every word must appear in a tool's name or description, so fewer words find more. Leave empty to list what is mounted.")] string query,
             [Description("Optional. Only search this MCP server, e.g. \"cockpit-session\".")] string? server = null,
             [Description("Optional. How many matches to return (default 10, max 50). The reply always says how many matched in total, so a truncated list is never mistaken for a complete one.")] int? limit = null)
                => _Search(catalog, query, server, limit),
            SearchToolName,
            "Finds the tools this session can reach that are not already loaded in your tool list. Returns each hit's server, name, description and JSON input schema — pass the arguments that schema describes to `call_tool` to run it. Search first whenever a capability you need is not among the tools you can see; the catalogue is kept out of your prompt precisely so it does not cost you context you are not using."),

        AIFunctionFactory.Create(
            // Name first so the schema marks it required and the optional two carry defaults — a nullable parameter
             // with no default is required in the generated schema, whatever its description says.
             ([Description("The tool's name, exactly as `search_tools` reported it.")] string name,
             [Description("The MCP server the tool lives on, as `search_tools` reported it. Optional only while the name is unique across the mounted servers.")] string? server = null,
             [Description("The tool's arguments as a JSON object, matching the `input_schema` from `search_tools` — e.g. {\"session\": \"abc\", \"status\": \"AC-963\"}.")] JsonElement? arguments = null,
             CancellationToken cancellationToken = default)
                => _CallAsync(catalog, server, name, arguments, cancellationToken),
            CallToolName,
            "Runs a tool found with `search_tools` and returns its result. Permissions are unchanged by going through here: the call is approved, refused or auto-allowed exactly as the same tool called directly would be."),
    ];

    private static string _Search(IReadOnlyList<McpSessionTool> catalog, string query, string? server, int? limit)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var scope = string.IsNullOrWhiteSpace(server)
            ? catalog
            : [.. catalog.Where(tool => string.Equals(tool.ServerName, server, StringComparison.OrdinalIgnoreCase))];

        if (!string.IsNullOrWhiteSpace(server) && scope.Count == 0)
        {
            var known = catalog.Select(tool => tool.ServerName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
            return _Json(new
            {
                matches = Array.Empty<object>(),
                total_matches = 0,
                note = $"No mounted server is called \"{server}\". Mounted servers: {string.Join(", ", known)}.",
            });
        }

        var terms = query?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        var ranked = scope
            .Select(tool => (Tool: tool, Score: _Score(tool, terms)))
            .Where(hit => hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Tool.Function.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matches = ranked.Take(take).Select(hit => new
        {
            server = hit.Tool.ServerName,
            name = hit.Tool.Function.Name,
            description = hit.Tool.Function.Description,
            input_schema = hit.Tool.Function.JsonSchema,
        }).ToArray();

        // Criterion 3: "nothing matched" and "more matched than you are seeing" must never read the same to a model.
        // Both get a sentence saying which of the two it is and what to do about it.
        var note = ranked.Count switch
        {
            0 => $"No tool matched \"{query}\". Every word has to appear in a tool's name or description — try fewer or more general words, or leave the query empty to list what is mounted.",
            _ when ranked.Count > matches.Length => $"Truncated: showing {matches.Length} of {ranked.Count} matches. Narrow the query, name a `server`, or raise `limit` (max {MaxLimit}).",
            _ => null,
        };

        return _Json(new { matches, total_matches = ranked.Count, note });
    }

    // Every term must appear somewhere; a term in the name counts double, so `set_status` outranks a tool that only
    // mentions status in prose. Substring matching over ~110 rows — BM25 or embeddings would be a search engine for
    // a list that fits on one screen.
    private static int _Score(McpSessionTool tool, string[] terms)
    {
        if (terms.Length == 0)
        {
            return 1;
        }

        var name = tool.Function.Name;
        var description = tool.Function.Description ?? string.Empty;
        var score = 0;

        foreach (var term in terms)
        {
            var inName = name.Contains(term, StringComparison.OrdinalIgnoreCase);
            var inDescription = description.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (!inName && !inDescription)
            {
                return 0;
            }

            score += inName ? 2 : 1;
        }

        return score;
    }

    private static async Task<string> _CallAsync(
        IReadOnlyList<McpSessionTool> catalog,
        string? server,
        string name,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return _Json(new { error = $"`name` is required — find a tool with `{SearchToolName}` first." });
        }

        var candidates = catalog
            .Where(tool => string.Equals(tool.Function.Name, name, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(server) || string.Equals(tool.ServerName, server, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (candidates.Count == 0)
        {
            return _Json(new
            {
                error = server is { Length: > 0 }
                    ? $"No tool called \"{name}\" on \"{server}\"."
                    : $"No tool called \"{name}\" is mounted in this session.",
                hint = $"Use `{SearchToolName}` to find the exact name and server.",
            });
        }

        // Two servers exposing the same tool name is a real arrangement, and picking one for the caller would run
        // the wrong tool as easily as the right one. Say which, and let it choose.
        if (candidates.Count > 1)
        {
            return _Json(new
            {
                error = $"\"{name}\" is on more than one mounted server: {string.Join(", ", candidates.Select(tool => tool.ServerName))}.",
                hint = "Pass `server` to say which one you mean.",
            });
        }

        var parsed = _ArgumentsOf(arguments);
        if (parsed is null)
        {
            return _Json(new { error = "`arguments` must be a JSON object of the tool's parameters, e.g. {\"path\": \"/tmp/x\"}." });
        }

        // The catalogue holds the session's *gated* functions, so this is the same instance — and the same approval,
        // tool class and AC-79 ceiling check under the real tool's name — a direct call would go through. A refusal
        // comes back as the tool result the gate wrote, which is what the model should see.
        var result = await candidates[0].Function.InvokeAsync(new AIFunctionArguments(parsed), cancellationToken).ConfigureAwait(false);
        return result?.ToString() ?? string.Empty;
    }

    // Null when the value is present but not an object. A local model that puts the object in a JSON *string* is
    // common enough to be worth one re-parse rather than an error it cannot act on.
    private static Dictionary<string, object?>? _ArgumentsOf(JsonElement? arguments)
    {
        if (arguments is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            try
            {
                using var reparsed = JsonDocument.Parse(text);
                return _ArgumentsOf(reparsed.RootElement.Clone());
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value)
            : null;
    }

    private static string _Json(object payload) => JsonSerializer.Serialize(payload, SerializerOptions);
}
