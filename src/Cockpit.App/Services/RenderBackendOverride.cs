using Avalonia;
using Cockpit.Core.Rendering;

namespace Cockpit.App.Services;

// AC-57 diagnostic probe: an opt-in override of the macOS render backend, read from the
// `COCKPIT_RENDER_BACKEND` environment variable. Off by default — an unset or unrecognised value leaves
// `UsePlatformDetect()`'s Metal auto-selection untouched — so it changes nothing for a normal run. A
// tester sets it to `opengl` or `software` to run the same build on a non-Metal path: the decisive
// test for whether the runaway native-memory growth on macOS is the Metal render layer. The options it
// produces (`AvaloniaNativePlatformOptions`) are read only by the macOS (Avalonia.Native) backend,
// so the override is inert on Windows and Linux.
//
// The env→modes mapping is a pure `Parse` so it is unit-testable without an Avalonia app or a Mac,
// the same reasoning as `TtyAutoRedrawGate`. Every option keeps `Software` as the last resort so a
// machine that cannot create the requested surface still starts rather than failing to a black window.
public static class RenderBackendOverride
{
    public const string EnvironmentVariable = "COCKPIT_RENDER_BACKEND";

    // The render backend to force and its human label, or null when no override is configured.
    public sealed record Selection(IReadOnlyList<AvaloniaNativeRenderingMode> Modes, string Label);

    // The override configured in the environment, or null when none is set (the default).
    public static Selection? FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    // The override the operator picked in Options (AC-67), or null for `RenderBackendChoice.Auto`.
    public static Selection? FromChoice(RenderBackendChoice choice) => choice switch
    {
        RenderBackendChoice.Metal => Parse("metal"),
        RenderBackendChoice.OpenGl => Parse("opengl"),
        RenderBackendChoice.Software => Parse("software"),
        _ => null,
    };

    // The override that startup actually applied, for the diagnostics panel to report — so a tester can confirm
    // the backend took effect regardless of whether it came from the env var or the saved setting. Null until
    // `Resolve` runs, and null when no override is active (platform default).
    public static Selection? Applied { get; private set; }

    // The effective override at startup: the environment variable wins (a per-launch escape hatch), otherwise the
    // saved `configChoice`. Records the result in `Applied` and returns it.
    public static Selection? Resolve(RenderBackendChoice configChoice)
    {
        Applied = FromEnvironment() ?? FromChoice(configChoice);
        return Applied;
    }

    // Maps a backend name to its render-mode priority list. Case- and whitespace-insensitive; an unknown, empty
    // or null value returns null — no override, the platform default stands.
    public static Selection? Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "metal" => new([AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software], "Metal"),
            "opengl" or "gl" => new([AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software], "OpenGL"),
            "software" or "sw" => new([AvaloniaNativeRenderingMode.Software], "Software"),
            _ => null,
        };
}
