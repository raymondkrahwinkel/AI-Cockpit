using Avalonia;
using Avalonia.Headless;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard;

// An Avalonia runtime without a screen. The painter draws fixed colours, not themed resources, so unlike the
// workflow canvas's fixture this needs no Theme.axaml load — Skia is enough to read real pixels back.
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
                .SetupWithoutStarting();

            _started = true;
        }
    }
}

[CollectionDefinition("avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<HeadlessAvalonia>;
