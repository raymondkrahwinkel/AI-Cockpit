using System.Globalization;

namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// A live counter/badge handle for a left-menu launcher button (AC-516): returned by
/// <see cref="ICockpitHost.AddSideMenuButtonWithBadge"/> and held by the plugin, which sets
/// <see cref="Primary"/>/<see cref="Secondary"/> whenever the number changes. The host re-renders on
/// <see cref="Changed"/> without polling.
/// </summary>
/// <remarks>
/// <see cref="Primary"/>/<see cref="Secondary"/> distinguish "not yet known" (<see langword="null"/>) from a
/// real zero ("0"). <see cref="Changed"/> may fire from any thread — a subscriber marshals to the UI thread
/// itself before touching a control.
/// </remarks>
public sealed class SideMenuButtonBadge
{
    private int? _primary;
    private int? _secondary;

    /// <summary>
    /// The main counter — the only one for a plugin that has just one number to show. <see langword="null"/>
    /// renders nothing ("not yet known"); <c>0</c> renders "0".
    /// </summary>
    public int? Primary
    {
        get => _primary;
        set
        {
            if (_primary == value)
            {
                return;
            }

            _primary = value;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// A second counter shown beside <see cref="Primary"/> ("3 / 2") — e.g. "mine" versus "waiting on me". The
    /// host's rendering ignores this while <see cref="Primary"/> is <see langword="null"/> (see the type doc): a
    /// secondary count means nothing without a primary one to sit next to.
    /// </summary>
    public int? Secondary
    {
        get => _secondary;
        set
        {
            if (_secondary == value)
            {
                return;
            }

            _secondary = value;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Raised whenever <see cref="Primary"/> or <see cref="Secondary"/> changes, so the host re-renders without polling.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// The host's own rendering rule (AC-516 acceptance criterion 3), exposed here so a plugin can preview exactly
    /// what the operator will see: <see langword="null"/> Primary renders as an empty string ("not yet known" —
    /// nothing shown); Primary alone renders as that number, including "0"; both set render as "primary / secondary".
    /// </summary>
    public string ToDisplayText() => this switch
    {
        { Primary: null } => string.Empty,
        { Primary: { } primary, Secondary: null } => primary.ToString(CultureInfo.InvariantCulture),
        { Primary: { } primary, Secondary: { } secondary } =>
            string.Create(CultureInfo.InvariantCulture, $"{primary} / {secondary}"),
    };
}
