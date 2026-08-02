namespace Cockpit.Plugin.LocalCi.Runtime;

// Whether the `act` runtime — the thing that actually reads a workflow and drives Docker — is available, and
// which version. The cockpit deliberately does not ship it: act is a per-platform binary of tens of megabytes
// that moves faster than the cockpit's own release cadence, so a
// bundled copy would be stale between releases and would cost every download for a feature most people never turn
// on. So the plugin says what to install instead of failing at the first run.
internal sealed record ActRuntimeStatus(bool IsInstalled, string? Version)
{
    public static ActRuntimeStatus NotInstalled { get; } = new(IsInstalled: false, null);

    public string Message => IsInstalled
        ? $"act {Version} is on PATH."
        : "act was not found on PATH. The cockpit does not ship it — install it with \"winget install nektos.act\" " +
          "(Windows), \"brew install act\" (macOS), or from github.com/nektos/act/releases.";
}
