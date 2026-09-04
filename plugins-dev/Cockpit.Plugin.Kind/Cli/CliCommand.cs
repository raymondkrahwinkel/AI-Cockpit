namespace Cockpit.Plugin.Kind.Cli;

// One kind invocation (AC-179): argv only, never a shell string, plus an environment layered onto the inherited
// one. KindCommand decides what goes in here; CliRunner only ever sees this shape.
internal sealed record CliCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string? StandardInput = null);
