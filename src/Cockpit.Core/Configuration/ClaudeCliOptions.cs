namespace Cockpit.Core.Configuration;

// Configuration for spawning the `claude` CLI in headless, persistent
// stream-json mode (see https://code.claude.com/docs/en/headless.md).
public sealed class ClaudeCliOptions
{
    // Executable name or full path. Defaults to "claude", resolved via PATH.
    public string ExecutablePath { get; set; } = "claude";

    // Working directory the CLI process is started in. Null uses the current directory.
    public string? WorkingDirectory { get; set; }

    // Permission mode passed via --permission-mode (default/acceptEdits/plan/bypassPermissions).
    // F-C1 default is "default" (prompts for anything not explicitly allowed), since the
    // interactive allow/deny UI is the first increment of permission handling.
    public string PermissionMode { get; set; } = "default";

    // Extra raw CLI arguments appended verbatim after the required stream-json flags.
    public IReadOnlyList<string> ExtraArguments { get; set; } = [];
}
