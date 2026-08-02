using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Workflows.Tests;

// An Avalonia runtime without a screen (#69). Controls ask the platform for things as ordinary as a mouse cursor,
// so they cannot even be constructed without one — this gives the tests a platform, once, so control-level bugs
// (a Button swallowing a pointer press, say) can be caught by a test rather than by the operator.
//
// It runs *with the host's theme loaded* (AC-337). A plugin draws inside the cockpit, so a canvas card built
// against a bare application is not the card anybody sees: every brush it asks for by name resolves to nothing and
// it falls back to Fluent. Cockpit.App cannot be referenced from here — a plugin that links the host is not a
// plugin — so `Styles/Theme.axaml` is read off disk and parsed, which is as close to the real thing as this
// side of the boundary can get.
//
// Set up by hand rather than with Avalonia.Headless.XUnit, which requires xunit v3 while this repo is on v2.
public sealed class HeadlessAvalonia
{
    private static readonly Lock Gate = new();
    private static bool _started;

    // AC-423 added a DataGridRow selector to the shared Theme.axaml parsed below. Nothing in this plugin ever
    // constructs a DataGrid, so Avalonia.Controls.DataGrid.dll — present in the output directory via the
    // PackageReference, but never touched by any executed code — would otherwise never actually load, and the
    // runtime XAML compiler only resolves a type against assemblies that are loaded. This reference forces it.
    private static readonly Type DataGridAssemblyAnchor = typeof(Avalonia.Controls.DataGridRow);

    public HeadlessAvalonia()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            // Skia rather than headless drawing: headless drawing stubs out text shaping, so a rendered card would
            // carry no glyphs and prove nothing about what it looks like.
            AppBuilder.Configure<Application>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                // The app ships Inter and asks for it at startup; a harness without it measures text in whatever
                // font the machine offers, which is not this program and not the same on CI.
                .WithInterFont()
                .AfterSetup(builder =>
                {
                    var application = builder.Instance
                        ?? throw new InvalidOperationException("Avalonia set up without an application instance.");

                    // Fluent first, then the icon set, then the cockpit's theme over both — the same order
                    // App.axaml uses, and the order matters: half of Theme.axaml exists to take states back off
                    // Fluent. Without the icon styles a MaterialIcon draws nothing at all, which in a render reads
                    // as a missing control rather than as a missing style.
                    application.Styles.Add(new FluentTheme());
                    application.Styles.Add(new MaterialIconStyles(null));
                    application.Styles.Add(CockpitTheme());

                    application.RequestedThemeVariant = ThemeVariant.Dark;
                })
                .SetupWithoutStarting();

            _started = true;
        }
    }

    // The host's `Theme.axaml`, parsed from the repository. Loudly, not best-effort: a theme that silently
    // failed to load would leave the render tests asserting Fluent's colours while reading as if they had checked
    // the cockpit's.
    private static IStyle CockpitTheme()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Cockpit.App", "Styles", "Theme.axaml");
            if (File.Exists(candidate))
            {
                return AvaloniaRuntimeXamlLoader.Load(File.ReadAllText(candidate)) as IStyle
                    ?? throw new InvalidOperationException($"{candidate} did not parse into a style.");
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "No src/Cockpit.App/Styles/Theme.axaml above the test output — these tests read the repo they belong to.");
    }
}

// Marks the tests that need a platform; xunit builds the fixture once for the whole collection.
[CollectionDefinition("avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<HeadlessAvalonia>;
