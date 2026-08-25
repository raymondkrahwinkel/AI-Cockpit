using Cockpit.Plugin.Kubernetes.Cli;

namespace Cockpit.Plugin.Kubernetes.Kind;

// PATH-probe for `kind` (AC-179 criterion 2), mirroring LocalCiRuntime._DetectActAsync: same 5s deadline, same
// "only a successful probe is cached" rule. `executableName`/`probeArguments` are a test seam only — production
// callers always take the defaults ("kind", "--version").
internal sealed class KindRuntime(ICliRunner runner, string executableName = "kind", IReadOnlyList<string>? probeArguments = null)
{
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment = new Dictionary<string, string>();

    private readonly IReadOnlyList<string> _probeArguments = probeArguments ?? ["--version"];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private KindRuntimeStatus? _cached;

    public async Task<KindRuntimeStatus> DetectAsync(CancellationToken cancellationToken)
    {
        if (_cached is { IsInstalled: true } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { IsInstalled: true } cachedAfterGate)
            {
                return cachedAfterGate;
            }

            var command = new CliCommand(executableName, _probeArguments, EmptyEnvironment);
            var result = await runner.RunAsync(command, ProbeTimeout, cancellationToken);
            if (!result.Succeeded)
            {
                return KindRuntimeStatus.NotInstalled;
            }

            var status = new KindRuntimeStatus(IsInstalled: true, _ReadVersion(result.Stdout));
            _cached = status;
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    // kind prints "kind v0.31.0 go1.25.5 linux/amd64" — the last whitespace-token, same parse shape as
    // LocalCiRuntime's _ReadActVersion.
    private static string? _ReadVersion(string output)
    {
        var line = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    }
}
