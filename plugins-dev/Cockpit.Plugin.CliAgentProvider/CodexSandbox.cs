namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// The Codex sandbox modes the cockpit offers, in one place so the launch options (SDK and TTY) and the live
/// sandbox control stay in sync. The launch paths pass the kebab value straight through as the app-server's
/// SandboxMode string on <c>thread/start</c>; the live per-turn override (#45 D4 inc2b) instead needs the
/// SandboxPolicy object's camelCase <c>type</c> discriminator, which <see cref="ToPolicyType"/> maps the same
/// kebab choice onto — so the operator sees one vocabulary while the wire gets the form each path requires.
/// </summary>
internal static class CodexSandbox
{
    public static IReadOnlyList<string> Choices { get; } = ["read-only", "workspace-write", "danger-full-access"];

    /// <summary>Maps a chosen kebab sandbox mode to the SandboxPolicy <c>type</c> discriminator, or <see langword="null"/> for an unknown one.</summary>
    public static string? ToPolicyType(string? mode) => mode switch
    {
        "read-only" => "readOnly",
        "workspace-write" => "workspaceWrite",
        "danger-full-access" => "dangerFullAccess",
        _ => null,
    };
}
