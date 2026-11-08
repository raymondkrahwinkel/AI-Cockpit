using Avalonia;
using Avalonia.Controls;
using Cockpit.App.Composition;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Layout;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    // AC-2209: a stand-in display while the assistant may look at it: the cyan frame round it, the strip at the top
    // with its cyan border, the time left and Stop, and the tab with the last look, placed where it covers no strip.
    private static Window _CompanionLookAlongScene(int width, int height)
    {
        var strip = new CompanionWindow(null);
        strip.SetTools(_CompanionSceneItems());
        strip.ApplyLayout(CompanionSceneOrientation == "v" ? CompanionOrientation.Vertical : CompanionOrientation.Horizontal, true);
        strip.ShowLookAlong("9:42");
        var canvas = new Canvas { Width = width, Height = height };
        canvas.Bind(Panel.BackgroundProperty, canvas.GetResourceObservable("CockpitWindowBgBrush"));
        canvas.Children.Add(strip.TakeCard());
        var frame = new Border { Width = width, Height = height, BorderThickness = new Thickness(3), IsHitTestVisible = false };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("CockpitStatusBusyBrush"));
        canvas.Children.Add(frame);
        canvas.Children.Add(LookAlongTabWindow.CreateCard(new TextBlock { FontSize = 12, Text = "Last look 14:02" }, () => { }));

        return new Window { Width = width, Height = height, Content = canvas };
    }

    private static void _StageCompanionLookAlong(Window scene)
    {
        var canvas = scene.Content as Canvas ?? throw new InvalidOperationException("The look-along scene needs its desktop.");
        var strip = canvas.Children[0];
        var tab = canvas.Children[2];
        var size = _Settled(scene, strip);
        var at = new PixelPoint(((int)canvas.Width - size.Width) / 2, 0);
        Canvas.SetLeft(strip, at.X);
        Canvas.SetTop(strip, at.Y);
        var tabSize = _Settled(scene, tab);
        var display = new PixelRect(0, 0, (int)canvas.Width, (int)canvas.Height);
        var spot = LookAlongTabWindow.Place(display, tabSize, [new PixelRect(at, size)]);
        if (new PixelRect(spot, tabSize).Intersects(new PixelRect(at, size)))
        {
            throw new InvalidOperationException("The look-along tab covers the strip.");
        }

        Canvas.SetLeft(tab, spot.X);
        Canvas.SetTop(tab, spot.Y);
        scene.UpdateLayout();
    }

    // AC-2209: the card look_at_screen raises, as the assistant's chat shows it, built by the real card builder.
    private static AssistantChatWindow _AssistantChatLookAtScreen()
    {
        var session = new ViewModels.SessionViewModel { Title = "Assistant" };
        session.Apply(new AssistantTextDelta { SessionId = "s1", BlockIndex = 0, Text = "May I look along on your screen? Then I can see the error dialog you mean." });
        session.Apply(new ToolUseRequested { SessionId = "s1", ToolUseId = "toolu_screen", ToolName = "mcp__cockpit-assistant-agents__look_at_screen", InputJson = "{\"reason\":\"To read the error dialog you mentioned.\"}" });
        DesktopDisplay[] displays =
        [
            new() { Bounds = new CaptureRect(0, 0, 2560, 1440), Scale = 1 },
            new() { Bounds = new CaptureRect(2560, 0, 1920, 1080), Scale = 1 },
        ];
        session.PendingConsent = new ViewModels.ConsentPromptViewModel(
            new Services.ConsentQuestion(Guid.NewGuid(), ScreenLookAlong.Card("To read the error dialog you mentioned.", displays), CanRemember: false),
            new _NoConsentPrompts(),
            inlineSlot: true);

        return new AssistantChatWindow { DataContext = _AssistantChatViewModel(session), Topmost = false, WindowStartupLocation = WindowStartupLocation.Manual };
    }
}
