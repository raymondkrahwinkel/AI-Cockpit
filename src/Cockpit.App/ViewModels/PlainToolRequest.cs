using System.Text.Json;

namespace Cockpit.App.ViewModels;

// AC-489: an approval restated from the call itself — the tool name and the structural keys of its input, never
// a key the model writes about its own intent (`description`, `prompt`, an Edit's replacement text): the agent
// describes its own request here, so a wrong sentence is a consent failure. Null for a call it cannot read.
public sealed record PlainToolRequest(string Sentence, IReadOnlyList<string> Paths)
{
    // Shell syntax that makes a line more than the one plain call read below: a pipeline, a sequence, a
    // redirect, a substitution, quoting, an expansion. Any of it and the tokens would describe a fragment of
    // what actually runs.
    private const string ShellSyntax = "|&;<>()`$'\"\\{}~!#\n\r";

    // Wildcards. Allowed — naming a month of invoices at once is how this audience works — but they are why no
    // count is claimed for them: what a pattern expands to is known only where the command runs.
    private const string Wildcards = "*?[]";

    private static readonly char[] Separators = ['/', '\\'];

    public static PlainToolRequest? Describe(string? toolName, string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return toolName switch
            {
                "Read" => _AboutFile(document.RootElement, "file_path", "Read the file"),
                "Write" => _AboutFile(document.RootElement, "file_path", "Create or replace the file"),
                "Edit" or "MultiEdit" => _AboutFile(document.RootElement, "file_path", "Change the file"),
                "NotebookEdit" => _AboutFile(document.RootElement, "notebook_path", "Change the notebook"),
                "Bash" => _AboutCommand(document.RootElement),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PlainToolRequest? _AboutFile(JsonElement root, string key, string verb) =>
        _Text(root, key) is { Length: > 0 } path
            ? new PlainToolRequest($"{verb} {_Leaf(path)}", [path])
            : null;

    private static PlainToolRequest? _AboutCommand(JsonElement root)
    {
        if (_Text(root, "command") is not { Length: > 0 } command || command.AsSpan().IndexOfAny(ShellSyntax) >= 0)
        {
            return null;
        }

        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        // A flag can turn the call into something else entirely — `rm -r` takes a tree, not a file — and reading
        // flags correctly is a shell parser. Only a command without them is described.
        if (tokens.Length < 2 || tokens.Any(token => token.StartsWith('-')))
        {
            return null;
        }

        var arguments = tokens[1..];
        return tokens[0] switch
        {
            "mv" => _Transfer(arguments, "Move"),
            "cp" => _Transfer(arguments, "Copy"),
            // Whole path, not the leaf: on the one verb nothing undoes, which `passwd` this is may not live only
            // in the file block beside the sentence. One file only — from two up, the count sends you there anyway.
            "rm" => new PlainToolRequest($"Delete {_Subjects(arguments, wholePath: true)}", arguments),
            "mkdir" when arguments.Length == 1 => new PlainToolRequest($"Create the folder {_Trim(arguments[0])}", []),
            _ => null,
        };
    }

    // `mv`/`cp` only when the destination is written as a directory. Without a trailing separator, `mv a b` is a
    // rename rather than a move into anything, and which of the two it is depends on a filesystem this
    // deliberately never reads.
    private static PlainToolRequest? _Transfer(string[] arguments, string verb)
    {
        if (arguments.Length < 2 || !_IsWrittenAsDirectory(arguments[^1]))
        {
            return null;
        }

        var sources = arguments[..^1];
        return new PlainToolRequest($"{verb} {_Subjects(sources)} into {_Trim(arguments[^1])}", sources);
    }

    private static string _Subjects(IReadOnlyList<string> paths, bool wholePath = false) =>
        paths.Any(_HasWildcard)
            ? $"the files matching {string.Join(" and ", paths)}"
            : paths.Count == 1
                ? wholePath ? paths[0] : _Leaf(paths[0])
                : $"{paths.Count} files";

    private static bool _HasWildcard(string path) => path.AsSpan().IndexOfAny(Wildcards) >= 0;

    private static bool _IsWrittenAsDirectory(string path) => path.EndsWith('/') || path.EndsWith('\\');

    private static string _Trim(string path) => path.TrimEnd(Separators) is { Length: > 0 } trimmed ? trimmed : path;

    private static string _Leaf(string path) =>
        path[(path.LastIndexOfAny(Separators) + 1)..] is { Length: > 0 } leaf ? leaf : path;

    private static string? _Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
