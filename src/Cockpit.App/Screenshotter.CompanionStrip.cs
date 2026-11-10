using Avalonia.Controls;
using Cockpit.App.Views;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    // The real strip on a stand-in screen, resting, as the mockup's "rest" scene. "--pos" picks the edge, "--orient" the
    // form; a strip that would run off the screen fails the scene.
    private static Window _CompanionStripScene(int width, int height)
    {
        var window = new CompanionWindow(null);
        window.SetTools(_CompanionSceneItems());

        return _CompanionDeskScene(width, height, new CompanionDesk(window, Position: CompanionScenePosition));
    }

    // The size a layout pass settles on, which a bare Measure of a control that was just changed does not give.
    private static Avalonia.PixelSize _Settled(Window scene, Control control)
    {
        scene.UpdateLayout();

        return new Avalonia.PixelSize((int)Math.Ceiling(control.DesiredSize.Width), (int)Math.Ceiling(control.DesiredSize.Height));
    }
}
