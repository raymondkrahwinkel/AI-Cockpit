namespace Cockpit.Infrastructure.ManagedCli;

// AC-1649: the server image splits the server (`app`) from the sessions (`agent`, deploy/README.md). The image sets
// these variables: the server downloads and verifies under <root>/.staging, the promote helper copies that into place
// as `agent`, and resolve hands out the wrapper on PATH, so the server never runs a managed binary itself.
internal sealed record ManagedCliAgentSplit(string PromoteHelper, string WrapperDirectory)
{
    private const string RootVariable = "COCKPIT_MANAGED_CLI_ROOT";
    private const string PromoteHelperVariable = "COCKPIT_MANAGED_CLI_PROMOTE";
    private const string WrapperDirectoryVariable = "COCKPIT_MANAGED_CLI_WRAPPERS";

    public static string? RootFromEnvironment() =>
        Environment.GetEnvironmentVariable(RootVariable) is { Length: > 0 } root ? root : null;

    public static ManagedCliAgentSplit? FromEnvironment() =>
        Environment.GetEnvironmentVariable(PromoteHelperVariable) is { Length: > 0 } helper
        && Environment.GetEnvironmentVariable(WrapperDirectoryVariable) is { Length: > 0 } wrappers
            ? new ManagedCliAgentSplit(helper, wrappers)
            : null;

    public string WrapperFor(string cliName) => Path.Combine(WrapperDirectory, cliName);
}
