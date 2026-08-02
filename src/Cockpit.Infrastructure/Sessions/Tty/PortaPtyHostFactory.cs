using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions.Tty;

// Linux/macOS `IPtyHostFactory`: spawns `PortaPtyProcess` (Porta.Pty).
// Registered only off Windows (`DependencyInjection.AddInfrastructure`).
internal sealed class PortaPtyHostFactory : IPtyHostFactory
{
    public IConPtyProcess Start(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        short columns,
        short rows) =>
        PortaPtyProcess.Start(executablePath, arguments, workingDirectory, environment, columns, rows);
}
