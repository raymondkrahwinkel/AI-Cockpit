using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-417: the generic plugin option rows in the New-session dialog gave the label a fixed 90px column, so
/// "Permission mode" — the longest label a Claude session renders — drew straight over the dropdown beside it.
/// A fixed column is only ever right until the next label, so what is asserted here is the property rather than
/// the number: the label's own text fits inside the slot it was given, and the rows line up as one column.
/// <para>
/// The label is measured against a <see cref="FormattedText"/> built from its own typeface rather than against
/// its <c>DesiredSize</c> — a measure pass clamps that to what it was offered, so an overflowing label reports a
/// width that fits while drawing past it. Nothing clips it; that is exactly why the operator sees the collision.
/// </para>
/// <para>
/// The rows are found through the <see cref="ItemsControl"/> bound to the collection under test, not by hunting
/// the window for label text: the dialog still carries the retired typed Claude rows, whose hidden "Model" and
/// "Effort" blocks are never arranged and would answer every question with a zero-width box.
/// </para>
/// </summary>
[Collection("avalonia")]
public class NewSessionPluginOptionRowTests
{
    // What a Claude TTY session actually renders, longest label first (the one that overflowed).
    private static readonly (string Key, string Label)[] ClaudeOptions =
    [
        ("permission-mode", "Permission mode"),
        ("model", "Model"),
        ("effort", "Effort"),
    ];

    /// <summary>The label column's cap, mirroring the <c>MaxWidth</c> in <c>NewSessionDialog.axaml</c>.</summary>
    private const double LabelColumnCap = 180;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionRows_GiveEveryLabelASlotItsTextFitsIn(bool sdk) => HeadlessAvalonia.Run(() =>
    {
        foreach (var label in _RenderedLabels(sdk, ClaudeOptions))
        {
            Assert.True(
                _NaturalWidth(label) <= label.Bounds.Width + 0.5,
                $"label '{label.Text}' needs {_NaturalWidth(label):F1}px but was given {label.Bounds.Width:F1}px, so it draws over the control beside it");
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionRows_ShareOneLeftEdgeAndOneColumnWidth(bool sdk) => HeadlessAvalonia.Run(() =>
    {
        var window = _Show(sdk, ClaudeOptions);
        var labels = _LabelsIn(window, sdk, ClaudeOptions);

        Assert.Equal(ClaudeOptions.Length, labels.Count);
        Assert.Single(labels.Select(label => Math.Round(label.Bounds.Width)).Distinct());
        Assert.Single(labels.Select(label => Math.Round(_LeftEdgeIn(window, label))).Distinct());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALabelLongerThanClaudesStillDoesNotOverrunItsSlot(bool sdk) => HeadlessAvalonia.Run(() =>
    {
        // The fix must hold for the next plugin's vocabulary too, not just for the label that reported the bug.
        var label = Assert.Single(_RenderedLabels(sdk, [("sandbox", "Sandbox permission policy")]));

        Assert.True(
            _NaturalWidth(label) <= label.Bounds.Width + 0.5,
            $"label '{label.Text}' needs {_NaturalWidth(label):F1}px but was given {label.Bounds.Width:F1}px");
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASentenceOfALabel_IsCappedAndTrimmed_RatherThanTakingTheWholeRow(bool sdk) => HeadlessAvalonia.Run(() =>
    {
        // A column that only sizes to content hands the whole row to a plugin that writes a sentence, leaving the
        // control it labels no width at all. Past the cap the label gives way — with the full text on a tooltip.
        (string Key, string Label)[] sentence = [("sandbox", "Sandbox permission policy for this provider's workspace")];
        var label = Assert.Single(_RenderedLabels(sdk, sentence));

        Assert.True(label.Bounds.Width < _NaturalWidth(label), "past the cap the label is the one that gives way");
        Assert.True(label.Bounds.Width <= LabelColumnCap, $"the label column grew to {label.Bounds.Width:F1}px, past its cap");
        Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
        Assert.Equal(sentence[0].Label, ToolTip.GetTip(label));
    });

    private static IReadOnlyList<TextBlock> _RenderedLabels(bool sdk, IReadOnlyList<(string Key, string Label)> options) =>
        _LabelsIn(_Show(sdk, options), sdk, options);

    /// <summary>Shows the dialog with the given options on the chosen route.</summary>
    private static Window _Show(bool sdk, IReadOnlyList<(string Key, string Label)> options)
    {
        var viewModel = new NewSessionDialogViewModel();
        var rows = sdk ? viewModel.SdkLaunchOptions : viewModel.PluginTtyOptions;
        foreach (var (key, label) in options)
        {
            rows.Add(new PluginTtyOptionSelectionViewModel(key, label, ["one", "two"], null));
        }

        if (sdk)
        {
            viewModel.SelectSdkCommand.Execute(null);
        }

        var window = new NewSessionDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    /// <summary>The rendered labels of the option rows, scoped to the host bound to those rows so the combo's placeholder and the retired typed rows stay out.</summary>
    private static IReadOnlyList<TextBlock> _LabelsIn(Window window, bool sdk, IReadOnlyList<(string Key, string Label)> options)
    {
        var viewModel = (NewSessionDialogViewModel)window.DataContext!;
        var rows = sdk ? viewModel.SdkLaunchOptions : viewModel.PluginTtyOptions;
        var host = window.GetVisualDescendants().OfType<ItemsControl>()
            .Single(items => ReferenceEquals(items.ItemsSource, rows));
        var wanted = options.Select(option => option.Label).ToHashSet();

        return [.. host.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.Text is { } text && wanted.Contains(text))];
    }

    /// <summary>How wide the label's text actually draws, independent of the slot layout gave it.</summary>
    private static double _NaturalWidth(TextBlock label) => new FormattedText(
        label.Text ?? string.Empty,
        CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        new Typeface(label.FontFamily, label.FontStyle, label.FontWeight),
        label.FontSize,
        Brushes.Black).Width;

    /// <summary>The label's left edge in window coordinates — each row is its own grid, so local bounds cannot show that the rows line up.</summary>
    private static double _LeftEdgeIn(Window window, TextBlock label) =>
        (label.TranslatePoint(new Point(0, 0), window) ?? default).X;
}
