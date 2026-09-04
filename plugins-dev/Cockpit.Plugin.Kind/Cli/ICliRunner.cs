namespace Cockpit.Plugin.Kind.Cli;

// An interface purely so a fake can stand in for tests that must not depend on the kind binary being on PATH.
internal interface ICliRunner
{
    Task<CliResult> RunAsync(CliCommand command, TimeSpan timeout, CancellationToken cancellationToken = default);
}
