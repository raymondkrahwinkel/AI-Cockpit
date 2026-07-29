using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Depot.Tests;

/// <summary>
/// An Avalonia runtime without a screen (AC-243, IL#9) — the same fixture <c>Cockpit.Plugin.Workflows.Tests</c>
/// built for #69, copied rather than shared because a plugin test project cannot reference another plugin's test
/// project any more than a plugin can reference the host. Runs <b>with the host's theme loaded</b>: a settings view
/// rendered against a bare application asks every named brush for nothing and falls back to Fluent, which is not
/// what the operator sees. <c>Cockpit.App</c> cannot be referenced from here, so <c>Styles/Theme.axaml</c> is read
/// off disk and parsed — as close to the real thing as this side of the plugin boundary can get.
/// <para>Set up by hand rather than with Avalonia.Headless.XUnit, which requires xunit v3 while this repo is on v2.</para>
/// </summary>
public sealed class HeadlessAvalonia
{
    private static readonly Lock Gate = new();
    private static bool _started;

    public HeadlessAvalonia()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            // Skia rather than headless drawing: headless drawing stubs out text shaping, so a rendered view would
            // carry no glyphs and prove nothing about what it looks like.
            AppBuilder.Configure<Application>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .AfterSetup(builder =>
                {
                    var application = builder.Instance
                        ?? throw new InvalidOperationException("Avalonia set up without an application instance.");

                    // Fluent first, then the icon set, then the cockpit's theme over both — the same order App.axaml
                    // uses, and the order matters: half of Theme.axaml exists to take states back off Fluent.
                    application.Styles.Add(new FluentTheme());
                    application.Styles.Add(new MaterialIconStyles(null));
                    application.Styles.Add(CockpitTheme());

                    application.RequestedThemeVariant = ThemeVariant.Dark;
                })
                .SetupWithoutStarting();

            _started = true;
        }
    }

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

/// <summary>Marks the tests that need a platform; xunit builds the fixture once for the whole collection.</summary>
[CollectionDefinition("avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<HeadlessAvalonia>;
