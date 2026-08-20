using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard;

// An Avalonia runtime without a screen. The painter draws fixed colours, not themed resources — FluentTheme is
// loaded anyway (AC-924), since a ContextMenu/Flyout Popup renders through the overlay layer only a themed
// Window template supplies.
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

            AppBuilder.Configure<Application>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .AfterSetup(_ => Application.Current!.Styles.Add(new FluentTheme()))
                .SetupWithoutStarting();

            _started = true;
        }
    }
}

[CollectionDefinition("avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<HeadlessAvalonia>;
