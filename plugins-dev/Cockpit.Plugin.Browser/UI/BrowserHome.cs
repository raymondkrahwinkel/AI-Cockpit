using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Material.Icons;

namespace Cockpit.Plugin.Browser.UI;

internal enum BrowserPlace
{
    Tab,
    Rail,
}

// Where the one browser shows (AC-1711): in the tab entry or in the rail, never both, since a control has one parent.
// Of the slots on screen, the newest of the chosen place holds the shell; the others say where it is. When no slot of
// that place is on screen, the first slot that appears takes it, so opening the browser anywhere shows it.
internal sealed class BrowserHome
{
    private readonly List<BrowserSlot> _attached = [];

    public BrowserHome(BrowserShell shell)
    {
        Shell = shell;
        shell.PlacementRequested += () =>
        {
            MoveTo(Place == BrowserPlace.Tab ? BrowserPlace.Rail : BrowserPlace.Tab);
            Moved?.Invoke(Place);
        };
    }

    public BrowserShell Shell { get; }

    public BrowserPlace Place { get; private set; } = BrowserPlace.Tab;

    // The operator moved the browser; the UI part brings that place on screen (the rail, or the tab).
    public event Action<BrowserPlace>? Moved;

    public BrowserSlot CreateSlot(BrowserPlace place) => new(this, place);

    public void MoveTo(BrowserPlace place)
    {
        Place = place;
        _Arrange();
    }

    internal void Attach(BrowserSlot slot)
    {
        _attached.Add(slot);
        if (_attached.Any(other => other.Place == Place && ReferenceEquals(other, slot) == false) == false)
        {
            Place = slot.Place;
        }

        _Arrange();
    }

    internal void Detach(BrowserSlot slot)
    {
        _attached.Remove(slot);
        _Arrange();
    }

    // Release first, then take: the shell must be out of its old parent before the new one can hold it.
    private void _Arrange()
    {
        var owner = _attached.LastOrDefault(slot => slot.Place == Place);
        foreach (var slot in _attached.Where(slot => ReferenceEquals(slot, owner) == false))
        {
            slot.ShowElsewhere();
        }

        if (owner is null && Shell.Parent is BrowserSlot holder)
        {
            holder.ShowElsewhere();
        }

        owner?.ShowShell();
        Shell.SetPlacement(
            Place == BrowserPlace.Tab ? "Dock in rail" : "Back to tab",
            Place == BrowserPlace.Tab ? MaterialIconKind.DockRight : MaterialIconKind.DockWindow,
            compact: Place == BrowserPlace.Rail);
    }
}

internal sealed class BrowserSlot : ContentControl
{
    private readonly BrowserHome _home;

    public BrowserSlot(BrowserHome home, BrowserPlace place)
    {
        _home = home;
        Place = place;
    }

    public BrowserPlace Place { get; }

    public void ShowShell()
    {
        if (ReferenceEquals(Content, _home.Shell) == false)
        {
            Content = _home.Shell;
        }
    }

    public void ShowElsewhere()
    {
        var bringBack = new Button { Content = "Bring it here", Classes = { "Compact", "Accent" }, HorizontalAlignment = HorizontalAlignment.Center };
        bringBack.Click += (_, _) => _home.MoveTo(Place);
        Content = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock
                {
                    Text = _home.Place == BrowserPlace.Rail ? "The browser is in the rail" : "The browser is open in a tab",
                    HorizontalAlignment = HorizontalAlignment.Center,
                }.Themed(TextBlock.ForegroundProperty, "CockpitTextSecondaryBrush"),
                bringBack,
            },
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _home.Attach(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _home.Detach(this);
    }
}
