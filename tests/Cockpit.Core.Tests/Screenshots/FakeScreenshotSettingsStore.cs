using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Screenshots;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>Screenshot settings held in memory rather than in <c>cockpit.json</c> — including the region a capture remembers for the next one (AC-329).</summary>
internal sealed class FakeScreenshotSettingsStore : IScreenshotSettingsStore
{
    public ScreenshotSettings Settings { get; private set; } = new();

    public Task<ScreenshotSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Settings);

    public Task SaveAsync(ScreenshotSettings settings, CancellationToken cancellationToken = default)
    {
        Settings = settings;
        return Task.CompletedTask;
    }
}
