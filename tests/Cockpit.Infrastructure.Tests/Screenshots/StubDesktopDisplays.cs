using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>A desktop that reports exactly the displays a test hands it — the screen list without a windowing system under it.</summary>
internal sealed class StubDesktopDisplays(IReadOnlyList<DesktopDisplay> displays) : IDesktopDisplays
{
    public Task<IReadOnlyList<DesktopDisplay>> EnumerateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(displays);
}
