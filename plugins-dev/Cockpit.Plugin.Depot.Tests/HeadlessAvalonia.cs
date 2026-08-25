using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Depot.Tests;

// An Avalonia runtime without a screen (AC-243, IL#9), copied rather than shared with `Cockpit.Plugin.Workflows.Tests`
// since a plugin test project cannot reference another plugin's test project. Loads the host's theme from disk
// so brushes resolve instead of falling back to Fluent; set up by hand since Avalonia.Headless.XUnit needs xunit v3, this repo is on v2.
public sealed class HeadlessAvalonia
{
    private static readonly Lock Gate = new();
    private static bool _started;

    // AC-423 added a DataGridRow selector to the shared Theme.axaml parsed below. Nothing in this plugin
    // constructs a DataGrid, so its assembly would never actually load, and the runtime XAML compiler only
    // resolves a type against loaded assemblies. This reference forces it.
    private static readonly Type DataGridAssemblyAnchor = typeof(Avalonia.Controls.DataGridRow);

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

// Marks the tests that need a platform; xunit builds the fixture once for the whole collection.
[CollectionDefinition("avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<HeadlessAvalonia>;
