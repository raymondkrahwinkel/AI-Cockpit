namespace Cockpit.Core.Configuration;

/// <summary>
/// Configuration for spawning the <c>claude</c> CLI in headless, persistent
/// stream-json mode (see https://code.claude.com/docs/en/headless.md).
/// </summary>
public sealed class ClaudeCliOptions
{
    /// <summary>
    /// Executable name or full path. Defaults to "claude", resolved via PATH.
    /// </summary>
    public string ExecutablePath { get; set; } = "claude";

    /// <summary>
    /// Working directory the CLI process is started in. Null uses the current directory.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Permission mode passed via --permission-mode (default/acceptEdits/plan/bypassPermissions).
    /// F-C1 default is "default" (prompts for anything not explicitly allowed), since the
    /// interactive allow/deny UI is the first increment of permission handling.
    /// </summary>
    public string PermissionMode { get; set; } = "default";

    /// <summary>
    /// Extra raw CLI arguments appended verbatim after the required stream-json flags.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; set; } = [];
}
