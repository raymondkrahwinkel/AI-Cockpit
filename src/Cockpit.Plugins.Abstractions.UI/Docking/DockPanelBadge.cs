using System.ComponentModel;

namespace Cockpit.Plugins.Abstractions.Docking;

/// <summary>
/// A count of what waits on the operator, drawn on a panel's rail tab — the Assistant's open questions.
/// The panel owns the number; the rail only draws it and reports a click on it.
/// </summary>
public sealed class DockPanelBadge : INotifyPropertyChanged
{
    private int _count;
    private SideMenuButtonBadgeTone _tone;

    /// <summary>
    /// The semantic tone, as on a side-menu badge: the default is amber, Success is green, Neutral is quiet.
    /// UI SDK 3.21.0, host 0.142.0 and later.
    /// </summary>
    public SideMenuButtonBadgeTone Tone
    {
        get => _tone;
        set
        {
            if (_tone == value)
            {
                return;
            }
            _tone = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tone)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised when the operator clicks the count itself, as opposed to the tab: the panel opens on what it counts.
    /// </summary>
    public event EventHandler? Activated;

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value)
            {
                return;
            }

            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCount)));
        }
    }

    public bool HasCount => Count > 0;

    public void Activate() => Activated?.Invoke(this, EventArgs.Empty);
}
