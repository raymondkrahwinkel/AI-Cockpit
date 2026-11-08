using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mentions;

namespace Cockpit.App.Controls;

// Draws a rounded background behind each accepted project token of Input, from the text layout's own geometry. It
// lies over the text box without taking input: filled for the token that sets the context, outlined for a mention.
public sealed class MentionTokenOverlay : Control
{
    public static readonly StyledProperty<TextBox?> InputProperty =
        AvaloniaProperty.Register<MentionTokenOverlay, TextBox?>(nameof(Input));

    public static readonly StyledProperty<MentionPickerViewModel?> PickerProperty =
        AvaloniaProperty.Register<MentionTokenOverlay, MentionPickerViewModel?>(nameof(Picker));

    // Whether the first project token fills (it sets the project) or is a mention like the rest (a running session).
    public static readonly StyledProperty<bool> FillsFirstTokenProperty =
        AvaloniaProperty.Register<MentionTokenOverlay, bool>(nameof(FillsFirstToken), true);

    static MentionTokenOverlay()
    {
        AffectsRender<MentionTokenOverlay>(InputProperty, PickerProperty, FillsFirstTokenProperty);
    }

    public MentionTokenOverlay()
    {
        IsHitTestVisible = false;
    }

    public TextBox? Input
    {
        get => GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public MentionPickerViewModel? Picker
    {
        get => GetValue(PickerProperty);
        set => SetValue(PickerProperty, value);
    }

    public bool FillsFirstToken
    {
        get => GetValue(FillsFirstTokenProperty);
        set => SetValue(FillsFirstTokenProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == InputProperty)
        {
            if (change.OldValue is TextBox old)
            {
                old.PropertyChanged -= _OnInputChanged;
            }

            if (change.NewValue is TextBox box)
            {
                box.PropertyChanged += _OnInputChanged;
            }
        }
        else if (change.Property == PickerProperty)
        {
            if (change.OldValue is MentionPickerViewModel old)
            {
                old.PropertyChanged -= _OnPickerChanged;
            }

            if (change.NewValue is MentionPickerViewModel picker)
            {
                picker.PropertyChanged += _OnPickerChanged;
            }
        }
    }

    private void _OnInputChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty || e.Property == BoundsProperty)
        {
            InvalidateVisual();
        }
    }

    private void _OnPickerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MentionPickerViewModel.Tokens))
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Picker is not { Tokens.Count: > 0 } picker
            || Input?.FindDescendantOfType<TextPresenter>() is not { TextLayout: { } layout } presenter
            || presenter.TranslatePoint(default, this) is not { } origin
            || Application.Current?.TryFindResource("CockpitAccentBrush", ActualThemeVariant, out var accent) != true
            || accent is not ISolidColorBrush accentBrush)
        {
            return;
        }

        var text = Input.Text ?? string.Empty;
        var fill = new SolidColorBrush(accentBrush.Color, 0.22);
        var outline = new Pen(accentBrush, 1);
        foreach (var token in picker.Tokens.Where(token => token.Item.Kind == MentionKind.Project && token.Start + token.Length <= text.Length))
        {
            var isContext = FillsFirstToken && token == picker.FirstProjectToken;
            foreach (var rect in layout.HitTestTextRange(token.Start, token.Length))
            {
                var box = new Rect(origin.X + rect.X - 3, origin.Y + rect.Y, rect.Width + 6, rect.Height).Deflate(new Thickness(0, 1));
                context.DrawRectangle(isContext ? fill : null, isContext ? null : outline, box, 5, 5);
            }
        }
    }
}
