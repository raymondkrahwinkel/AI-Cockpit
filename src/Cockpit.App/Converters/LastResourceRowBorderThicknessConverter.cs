using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Cockpit.App.Converters;

/// <summary>
/// The bottom border every resource row draws to separate itself from the next one (AC-485) — except the last row,
/// which has no next row to separate itself from and so must draw none.
/// <para>
/// AC-485 review (FIX 8): a plain per-item <c>Border</c> in the row's own <c>DataTemplate</c> drew this bottom line
/// unconditionally, so the last row hung a hairline under itself with nothing beneath it but the "+ Add row"
/// button — a divider for a row that was not there.
/// </para>
/// <para>
/// Two other approaches were tried and measured not to work before this one: a structural CSS-style selector
/// (<c>ContentPresenter:nth-last-child(1) Border</c>) does not reach across the
/// <see cref="Avalonia.Controls.Presenters.ContentPresenter"/> boundary a data-templated item's content sits
/// behind — the pseudo-class matched the presenter itself, but a style setter on a descendant selector past it
/// never applied. A <c>MultiBinding</c> comparing the row against the dialog's own <c>ResourceRows</c> list applied
/// correctly at first render but not reliably after a row was added or removed — <c>ResourceRows</c> is the same
/// collection reference for the dialog's whole lifetime, so nothing about a row being added or removed forces the
/// binding engine to re-run the comparison, and it was observed to answer with a stale, pre-mutation reading. See
/// <see cref="ProjectResourceRowViewModel.IsLastRow"/>'s own remarks — a plain property the dialog sets explicitly
/// whenever <c>ResourceRows</c> actually changes cannot go stale the same way, and this converter now just reads it.
/// </para>
/// </summary>
public sealed class LastResourceRowBorderThicknessConverter : IValueConverter
{
    public static readonly LastResourceRowBorderThicknessConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new Thickness(0) : new Thickness(0, 0, 0, 1);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
