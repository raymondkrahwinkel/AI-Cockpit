using Avalonia;
using Avalonia.Controls;
using Cockpit.App.Composition;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    // AC-2209: a stand-in display while the assistant may look at it: the cyan frame round it, the strip at the top
    // right with its cyan border and the look-along pill, and the tab with the last look, placed where it covers no
    // strip — the mockup's "look" scene.
    private static Window _CompanionLookAlongScene(int width, int height)
    {
        var strip = new CompanionWindow(null);
        strip.SetTools(_CompanionSceneItems());
        strip.ShowLookAlong("9:42");

        return _CompanionDeskScene(width, height, new CompanionDesk(strip, Extra: (canvas, stripRect) =>
        {
            var frame = new Border { Width = width, Height = height, BorderThickness = new Thickness(3), IsHitTestVisible = false };
            frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("CockpitStatusBusyBrush"));
            canvas.Children.Add(frame);
            var tab = LookAlongTabWindow.CreateCard(new TextBlock { Text = "last look 14:02" }, () => { });
            canvas.Children.Add(tab);
            tab.Measure(Size.Infinity);
            var tabSize = new PixelSize((int)Math.Ceiling(tab.DesiredSize.Width), (int)Math.Ceiling(tab.DesiredSize.Height));
            var spot = LookAlongTabWindow.Place(new PixelRect(0, 0, width, height), tabSize, [_Inflate(stripRect, CompanionPopOutCard.ShadowMargin)]);
            if (new PixelRect(spot, tabSize).Intersects(_Inflate(stripRect, CompanionPopOutCard.ShadowMargin)))
            {
                throw new InvalidOperationException("The look-along tab covers the strip.");
            }

            Canvas.SetLeft(tab, spot.X);
            Canvas.SetTop(tab, spot.Y);
        }));
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
