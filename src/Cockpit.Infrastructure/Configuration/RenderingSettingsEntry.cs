using Cockpit.Core.Rendering;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// The <c>rendering</c> section of <c>cockpit.json</c> (AC-67): the operator's render-backend choice. The enum
/// round-trips as its name via the shared <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>.
/// </summary>
internal sealed class RenderingSettingsEntry
{
    public RenderBackendChoice Backend { get; set; } = RenderBackendChoice.Auto;

    public static RenderingSettingsEntry FromDomain(RenderingSettings settings) =>
        new() { Backend = settings.Backend };

    public RenderingSettings ToDomain() => new() { Backend = Backend };
}
