using Cockpit.Core.Rendering;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// Reads just the persisted render backend from <c>cockpit.json</c> for <c>Program.BuildAvaloniaApp()</c>'s
/// pre-container pass (AC-67), where the DI host — and the internal section stores — do not exist yet. Public on
/// purpose (like <c>PluginBootstrap</c>): callable with <c>new</c>/statically before the container is built.
/// <para>
/// Returns <see cref="RenderBackendChoice.Auto"/> (no override) on any absence or read error — a missing or broken
/// config must never stop the app from starting, and the render backend is a diagnostic knob, not a hard setting.
/// Reuses the same deserialization as the writer, so it stays correct if the on-disk shape evolves; the encrypted
/// credential fields are irrelevant here since the backend is a non-secret plaintext value read alongside them.
/// </para>
/// </summary>
public static class RenderBackendConfig
{
    public static RenderBackendChoice Read() => Read(CockpitConfigPath.Default);

    internal static RenderBackendChoice Read(string configFilePath)
    {
        try
        {
            var configFile = new CockpitConfigFileAccess(configFilePath)
                .ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
            return configFile?.Rendering?.ToDomain().Backend ?? RenderBackendChoice.Auto;
        }
        catch
        {
            return RenderBackendChoice.Auto;
        }
    }
}
