using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Plugin.Depot.Files;

internal sealed class DepotFileSourceProvider(ICockpitHost host,
    Func<IReadOnlyList<DepotConnectionRegistration>> connections) : IFileSourceProvider
{
    public IReadOnlyList<FileConnection> Connections =>
        [.. connections().Select(c => new FileConnection(c.Id, c.Name, "depot", false))];
    public event EventHandler? ConnectionsChanged;
    public void NotifyChanged() => ConnectionsChanged?.Invoke(this, EventArgs.Empty);
    public IFileSource Open(string connectionId) => new DepotFileSource();

    private sealed class DepotFileSource : IFileSource
    {
        public FileSourceCapabilities Capabilities { get; } = new(true, FileSourceChannel.MaxContentBytes);
        public Task<FileReadResult> ReadAsync(FileRef file, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Depot file reads are not implemented yet.");
        public Task<FileWriteResult> WriteAsync(FileRef file, Stream content, string? ifVersion,
            bool mustNotExist, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Depot conditional writes are not implemented yet.");
    }
}
