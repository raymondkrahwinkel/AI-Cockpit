using Cockpit.Core.Abstractions.Claude;

namespace Cockpit.Infrastructure.Claude.Tty;

/// <summary>
/// Linux/macOS <see cref="IPtyHostFactory"/>: spawns <see cref="PortaPtyProcess"/> (Porta.Pty).
/// Registered only off Windows (<c>DependencyInjection.AddInfrastructure</c>).
/// </summary>
internal sealed class PortaPtyHostFactory : IPtyHostFactory
{
    public IConPtyProcess Start(
        string executablePath,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        short columns,
        short rows) =>
        PortaPtyProcess.Start(executablePath, workingDirectory, environment, columns, rows);
}
