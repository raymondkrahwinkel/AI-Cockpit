using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitStatus.Tests;

// An `IPluginSessionContext` whose working directory a test sets at construction and can change or
// send output through by hand — enough to drive `GitStatusHeaderControl` without a real session.
internal sealed class FakeSessionContext(string? workingDirectory) : IPluginSessionContext
{
    public string? WorkingDirectory { get; private set; } = workingDirectory;

    public event EventHandler? WorkingDirectoryChanged;

    public event EventHandler<SessionOutputText>? OutputProduced;

    public void ChangeWorkingDirectory(string? directory)
    {
        WorkingDirectory = directory;
        WorkingDirectoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ProduceOutput(string text) => OutputProduced?.Invoke(this, new SessionOutputText(text, WorkingDirectory, IsFromActiveSession: true));
}
