using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.Core.SessionSwitching;
using SvcSystems.UI.Terminal;

namespace Cockpit.App.Views;

public partial class CockpitView : UserControl
{
    public CockpitView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Handle the switch gesture on the top-level (window) so it fires regardless of which panel
        // has focus. Tunnelling (handledEventsToo not needed) would pre-empt a focused TextBox's own
        // Ctrl+Left/Right word-navigation, so we listen on the bubbling KeyDown and bail out when the
        // focus sits in an editable element — that keeps word-nav intact while typing.
        if (e.Root is InputElement root)
        {
            root.AddHandler(KeyDownEvent, OnRootKeyDown, RoutingStrategies.Bubble);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (e.Root is InputElement root)
        {
            root.RemoveHandler(KeyDownEvent, OnRootKeyDown);
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        var settings = cockpit.CurrentSessionSwitchSettings;
        if (!settings.IsEnabled)
        {
            return;
        }

        if (_TryGetSwitchDirection(e.Key) is not { } direction)
        {
            return;
        }

        if (e.KeyModifiers != _RequiredModifiers(settings.Modifier))
        {
            return;
        }

        // Focus-conflict guard: never steal the gesture while the user is typing. Ctrl+Left/Right is
        // word-navigation in a TextBox, and the terminal owns all keystrokes in TTY mode.
        if (_IsFocusInEditableElement())
        {
            return;
        }

        if (direction < 0)
        {
            cockpit.SelectPreviousSession();
        }
        else
        {
            cockpit.SelectNextSession();
        }

        e.Handled = true;
    }

    /// <summary>Left/Up = previous (-1), Right/Down = next (+1); any other key = not a switch gesture.</summary>
    private static int? _TryGetSwitchDirection(Key key) => key switch
    {
        Key.Left or Key.Up => -1,
        Key.Right or Key.Down => 1,
        _ => null,
    };

    private static KeyModifiers _RequiredModifiers(SessionSwitchModifier modifier) => modifier switch
    {
        SessionSwitchModifier.CtrlAlt => KeyModifiers.Control | KeyModifiers.Alt,
        SessionSwitchModifier.Alt => KeyModifiers.Alt,
        _ => KeyModifiers.Control,
    };

    private bool _IsFocusInEditableElement()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return focused switch
        {
            TextBox => true,
            TerminalControl => true,
            Visual visual => visual.FindAncestorOfType<TerminalControl>() is not null,
            _ => false,
        };
    }

    /// <summary>Sidebar item click → select that session. Plain event handler (not a command) since the
    /// clicked session is the DataContext of the <see cref="Border"/> raising the event, not the item passed
    /// as a bindable CommandParameter — simplest wiring for a whole-row click target.</summary>
    private void OnSessionItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: SessionPanelViewModel session } && DataContext is CockpitViewModel cockpit)
        {
            cockpit.SelectSessionCommand.Execute(session);
        }
    }
}
