using Cockpit.Core.Rendering;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Rendering;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Rendering;

/// <summary>
/// The render-backend choice (AC-67) is written by the store but read back two ways: by the store itself for the
/// Options UI, and — critically — by <see cref="RenderBackendConfig"/> in Program's pre-container pass, which
/// configures Avalonia at startup. Both must agree with what was saved, or the setting would show one backend and
/// the app would start on another.
/// </summary>
public sealed class RenderingSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-render-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_directory, "cockpit.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Save_RoundTripsThroughBothTheStoreAndTheStartupReader()
    {
        Directory.CreateDirectory(_directory);
        var store = new RenderingSettingsStore(ConfigPath);

        await store.SaveAsync(new RenderingSettings { Backend = RenderBackendChoice.OpenGl });

        (await store.LoadAsync()).Backend.Should().Be(RenderBackendChoice.OpenGl);
        // The early, pre-container reader must see exactly what the store wrote.
        RenderBackendConfig.Read(ConfigPath).Should().Be(RenderBackendChoice.OpenGl);
    }

    [Fact]
    public async Task DefaultsToAuto_WhenNothingWasSaved()
    {
        Directory.CreateDirectory(_directory);

        (await new RenderingSettingsStore(ConfigPath).LoadAsync()).Backend.Should().Be(RenderBackendChoice.Auto);
        RenderBackendConfig.Read(ConfigPath).Should().Be(RenderBackendChoice.Auto);
    }
}
