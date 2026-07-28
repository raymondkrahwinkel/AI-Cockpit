using Avalonia.Controls;
using Avalonia.Layout;

namespace Cockpit.Plugin.FanOut;

/// <summary>
/// One row of the set-up form: the profile this arm runs on and the angle it takes. Both are offered on every
/// row because the two ways of fanning out — several providers on one brief, several angles on one provider —
/// are the same run with a different column filled in, and the operator should not have to pick a mode first.
/// </summary>
internal sealed class FanOutVariantEditor
{
    private readonly ComboBox _profile;
    private readonly TextBox _angle;

    public FanOutVariantEditor(string placeholder)
    {
        _profile = new ComboBox
        {
            Width = 190,
            PlaceholderText = "Default profile",
            VerticalAlignment = VerticalAlignment.Center,
        };

        _angle = new TextBox
        {
            PlaceholderText = placeholder,
            VerticalAlignment = VerticalAlignment.Center,
            [Grid.ColumnProperty] = 1,
        };

        var remove = new Button
        {
            Content = "✕",
            VerticalAlignment = VerticalAlignment.Center,
            [Grid.ColumnProperty] = 2,
        };
        remove.Click += (_, _) => RemoveRequested?.Invoke(this, EventArgs.Empty);

        View = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            Children = { _profile, _angle, remove },
        };
    }

    public Control View { get; }

    /// <summary>Raised when this row's ✕ is pressed; the form decides whether the row may actually go.</summary>
    public event EventHandler? RemoveRequested;

    /// <summary>
    /// Fills the profile picker once the host has answered. Rows start on different profiles where there are
    /// enough to go round, so a run that varies the provider is set up by typing nothing at all.
    /// </summary>
    public void ShowProfiles(IReadOnlyList<string> profiles, int preferredIndex)
    {
        _profile.ItemsSource = profiles;
        if (profiles.Count > 0)
        {
            _profile.SelectedIndex = Math.Min(preferredIndex, profiles.Count - 1);
        }
    }

    public FanOutVariant ToVariant() => new(_profile.SelectedItem as string ?? string.Empty, _angle.Text ?? string.Empty);
}
