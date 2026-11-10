using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.Plugins.Shared;

// Known gap: interpreters (python -c, node -e) and other indirect writes pass the shell heuristic; the shell boundary is AC-2252/AC-2253 (B4).
internal static class ClaudePathGuard
{
    public const string ModeVariable = "COCKPIT_GUARD_MODE";
    public const string RootsVariable = "COCKPIT_GUARD_ROOTS";

    private static readonly HashSet<string> _PowerShellWriters = new(StringComparer.OrdinalIgnoreCase)
        { "out-file", "set-content", "add-content", "new-item", "copy-item", "move-item", "remove-item" };
    private static readonly HashSet<string> _PowerShellSwitches = new(StringComparer.OrdinalIgnoreCase)
        { "-force", "-recurse", "-append", "-noclobber", "-nonewline", "-whatif", "-confirm", "-passthru", "-container", "-asbytestream" };
    private static readonly HashSet<string> _LocationCommands = new(StringComparer.OrdinalIgnoreCase)
        { "cd", "chdir", "pushd", "set-location", "push-location", "sl" };
    private static readonly HashSet<string> _Discards = new(StringComparer.OrdinalIgnoreCase)
        { "/dev/null", "/dev/stdout", "/dev/stderr", "nul", "$null" };

    public static GuardrailMode? ModeFrom(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Enum.TryParse<GuardrailMode>(value, ignoreCase: true, out var mode) ? mode : GuardrailMode.Strict;

    public static IReadOnlyList<string> RootsFrom(string? value) =>
        (value ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static GuardDecision Evaluate(string toolName, string toolInputJson, string? cwd, GuardrailMode mode, IReadOnlyList<string> roots, KillScope? killScope = null)
    {
        if (mode == GuardrailMode.Off)
        {
            return GuardDecision.Allow;
        }
        JsonObject? input;
        try
        {
            input = JsonNode.Parse(toolInputJson) as JsonObject;
        }
        catch (JsonException)
        {
            input = null;
        }
        string? Text(string key) => input?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var directory = cwd is { Length: > 0 } && Path.IsPathFullyQualified(cwd) ? cwd : Environment.CurrentDirectory;

        var outside = toolName switch
        {
            "Write" or "Edit" or "MultiEdit" => _Outside(Text("file_path"), directory, roots, shell: false),
            "NotebookEdit" => _Outside(Text("notebook_path"), directory, roots, shell: false),
            "Bash" or "PowerShell" when Text("command") is { } command => _ShellOutside(command, directory, roots),
            _ => null,
        };
        var decision = outside is null ? GuardDecision.Allow : mode == GuardrailMode.Strict
            ? new(true, $"'{outside}' is outside the session boundary; use $COCKPIT_SCRATCH or the worktree.")
            : new(false, $"Cockpit guard (warn): '{outside}' is outside the session boundary; use $COCKPIT_SCRATCH or the worktree.");
        return killScope is not null && toolName is "Bash" or "PowerShell" && Text("command") is { } shellCommand
            ? decision.With(CommandGuard.EvaluateKill(shellCommand, killScope, mode))
            : decision;
    }

    // The hook's stdout for one decision, or null when there is nothing to say.
    public static string? HookOutput(GuardDecision decision) => decision.Message is null ? null : new JsonObject
    {
        ["hookSpecificOutput"] = decision.Deny
            ? new JsonObject { ["hookEventName"] = "PreToolUse", ["permissionDecision"] = "deny", ["permissionDecisionReason"] = decision.Message }
            : new JsonObject { ["hookEventName"] = "PreToolUse", ["additionalContext"] = decision.Message },
    }.ToJsonString();

    // The relay's `--guard` entry: hook input on stdin, decision on stdout.
    public static void RunHook(TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (ModeFrom(Environment.GetEnvironmentVariable(ModeVariable)) is not { } mode || mode == GuardrailMode.Off)
        {
            return;
        }
        JsonObject? data;
        try
        {
            data = JsonNode.Parse(stdin.ReadToEnd()) as JsonObject;
        }
        catch (JsonException exception)
        {
            // Known fail-open: unreadable hook input gets no decision, so the tool call goes on unguarded.
            stderr.WriteLine($"cockpit-guard: unreadable hook input, no decision: {exception.Message}");
            return;
        }
        string? Text(string key) => data?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var root = int.TryParse(Environment.GetEnvironmentVariable(CommandGuard.HostPidVariable), out var host) ? CommandGuard.RootBelow(host, Environment.ProcessId) : null;
        var decision = Evaluate(Text("tool_name") ?? string.Empty, data?["tool_input"]?.ToJsonString() ?? "{}", Text("cwd"), mode,
            RootsFrom(Environment.GetEnvironmentVariable(RootsVariable)),
            CommandGuard.SessionScope(root, Environment.GetEnvironmentVariable(CommandGuard.PaneVariable), Text("cwd") ?? Environment.CurrentDirectory,
                Environment.GetEnvironmentVariable(CommandGuard.JobVariable)));
        if (HookOutput(decision) is { } output)
        {
            stdout.Write(output);
            stderr.WriteLine($"cockpit-guard session {Text("session_id")}: {decision.Message}");
        }
    }

    private static string? _Outside(string? path, string cwd, IReadOnlyList<string> roots, bool shell = true)
    {
        if ((shell ? _Resolve(path, cwd) : _FullPath(path, cwd)) is not { } full)
        {
            return null;
        }
        return PathBoundary.Evaluate(full, roots).IsInside ? null : full;
    }

    private static string? _FullPath(string? path, string cwd)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path, cwd);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // A shell word: variables, ~ and Git Bash drive paths expanded; null when it cannot be told.
    private static string? _Resolve(string? path, string cwd)
    {
        if (string.IsNullOrWhiteSpace(path) || _Discards.Contains(path) || path.StartsWith('&'))
        {
            return null;
        }
        var value = Regex.Replace(path,@"\$env:(\w+)|\$\{(\w+)\}|\$(\w+)|%(\w+)%", match =>
        {
            var name = new[] { match.Groups[1], match.Groups[2], match.Groups[3], match.Groups[4] }.First(group => group.Success).Value;
            return Environment.GetEnvironmentVariable(name)
                ?? (name.Equals("HOME", StringComparison.OrdinalIgnoreCase) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : match.Value);
        });
        if (value.Contains('$') || value.Contains('`') || value.Contains('%'))
        {
            return null;
        }
        if (value == "~" || value.StartsWith("~/") || value.StartsWith("~\\"))
        {
            value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[Math.Min(2, value.Length)..]);
        }
        if (OperatingSystem.IsWindows() && Regex.Match(value, "^/([a-zA-Z])(/|$)") is { Success: true } drive)
        {
            value = drive.Groups[1].Value + ":\\" + value[Math.Min(3, value.Length)..];
        }
        return _FullPath(value, cwd);
    }

    private static string? _ShellOutside(string command, string cwd, IReadOnlyList<string> roots)
    {
        foreach (var words in CommandGuard.Segments(command))
        {
            var targets = new List<string>();
            var arguments = new List<string>();
            for (var index = 0; index < words.Count; index++)
            {
                var redirect = Regex.Match(words[index], @"^\d*>>?(.*)$");
                if (redirect.Success)
                {
                    var attached = redirect.Groups[1].Value;
                    targets.Add(attached.Length > 0 ? attached : index + 1 < words.Count ? words[++index] : string.Empty);
                }
                else
                {
                    arguments.Add(words[index]);
                }
            }
            if (arguments.Count > 0)
            {
                var name = Path.GetFileNameWithoutExtension(arguments[0]).ToLowerInvariant();
                var rest = arguments.Skip(1).ToList();
                if (_LocationCommands.Contains(name))
                {
                    var location = rest.Count == 0 ? "~" : rest.FirstOrDefault(word => !word.StartsWith('-'));
                    if (location is not null && _Resolve(location, cwd) is { } moved)
                    {
                        cwd = moved;
                    }
                    continue;
                }
                targets.AddRange(_PowerShellWriters.Contains(name) ? _PowerShellTargets(name, rest) : name switch
                {
                    "tee" or "touch" or "mkdir" or "rm" or "rmdir" => rest.Where(word => !word.StartsWith('-')),
                    "cp" or "mv" => rest.Where(word => !word.StartsWith('-')).TakeLast(1),
                    _ => [],
                });
            }
            if (targets.Select(target => _Outside(target, cwd, roots)).FirstOrDefault(found => found is not null) is { } outside)
            {
                return outside;
            }
        }
        return null;
    }

    private static IEnumerable<string> _PowerShellTargets(string cmdlet, List<string> arguments)
    {
        var named = new List<string>();
        var positional = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var word = arguments[index];
            if (!word.StartsWith('-') || word == "-")
            {
                positional.Add(word);
                continue;
            }
            var colon = word.IndexOf(':');
            var parameter = (colon > 0 ? word[..colon] : word).ToLowerInvariant();
            if (_PowerShellSwitches.Contains(parameter))
            {
                continue;
            }
            var value = colon > 0 ? word[(colon + 1)..] : index + 1 < arguments.Count ? arguments[++index] : null;
            var isCopy = cmdlet is "copy-item" or "move-item";
            if (value is not null && (parameter == "-destination" || !isCopy && parameter is "-path" or "-literalpath" or "-filepath"))
            {
                named.Add(value);
            }
            else if (isCopy && parameter is "-path" or "-literalpath")
            {
                positional.Insert(0, string.Empty);
            }
        }
        var position = cmdlet is "copy-item" or "move-item" ? 1 : 0;
        return named.Count > 0 || positional.Count <= position ? named : [positional[position]];
    }
}
