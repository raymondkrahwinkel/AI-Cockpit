using Cockpit.Core.Rendering;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Rendering;

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

    // One exercise over the whole contract: what the two readers say before anything is saved, and that they still
    // agree afterwards. The backend choice is not a security boundary, so "something here broke" is diagnosis enough.
    [Fact]
    public async Task TheBackendChoice_ReadsAutoUntilOneIsSaved_AndBothReadersAgreeEitherWay()
    {
        Directory.CreateDirectory(_directory);
        var store = new RenderingSettingsStore(ConfigPath);

        Assert.Equal(RenderBackendChoice.Auto, (await store.LoadAsync()).Backend);
        Assert.Equal(RenderBackendChoice.Auto, RenderBackendConfig.Read(ConfigPath));

        await store.SaveAsync(new RenderingSettings { Backend = RenderBackendChoice.OpenGl });

        Assert.Equal(RenderBackendChoice.OpenGl, (await store.LoadAsync()).Backend);
        // The early, pre-container reader must see exactly what the store wrote.
        Assert.Equal(RenderBackendChoice.OpenGl, RenderBackendConfig.Read(ConfigPath));
    }
}
