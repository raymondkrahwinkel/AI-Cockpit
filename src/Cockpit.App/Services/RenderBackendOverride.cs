using Avalonia;

namespace Cockpit.App.Services;

/// <summary>
/// AC-57 diagnostic probe: an opt-in override of the macOS render backend, read from the
/// <c>COCKPIT_RENDER_BACKEND</c> environment variable. Off by default — an unset or unrecognised value leaves
/// <c>UsePlatformDetect()</c>'s Metal auto-selection untouched — so it changes nothing for a normal run. A
/// tester sets it to <c>opengl</c> or <c>software</c> to run the same build on a non-Metal path: the decisive
/// test for whether the runaway native-memory growth on macOS is the Metal render layer. The options it
/// produces (<see cref="AvaloniaNativePlatformOptions"/>) are read only by the macOS (Avalonia.Native) backend,
/// so the override is inert on Windows and Linux.
/// <para>
/// The env→modes mapping is a pure <see cref="Parse"/> so it is unit-testable without an Avalonia app or a Mac,
/// the same reasoning as <c>TtyAutoRedrawGate</c>. Every option keeps <c>Software</c> as the last resort so a
/// machine that cannot create the requested surface still starts rather than failing to a black window.
/// </para>
/// </summary>
public static class RenderBackendOverride
{
    public const string EnvironmentVariable = "COCKPIT_RENDER_BACKEND";

    /// <summary>The render backend to force and its human label, or null when no override is configured.</summary>
    public sealed record Selection(IReadOnlyList<AvaloniaNativeRenderingMode> Modes, string Label);

    /// <summary>The override configured in the environment, or null when none is set (the default).</summary>
    public static Selection? FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// Maps a backend name to its render-mode priority list. Case- and whitespace-insensitive; an unknown, empty
    /// or null value returns null — no override, the platform default stands.
    /// </summary>
    public static Selection? Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "metal" => new([AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software], "Metal"),
            "opengl" or "gl" => new([AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software], "OpenGL"),
            "software" or "sw" => new([AvaloniaNativeRenderingMode.Software], "Software"),
            _ => null,
        };
}
