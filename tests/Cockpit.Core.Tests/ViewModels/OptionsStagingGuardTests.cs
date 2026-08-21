using System.Text.RegularExpressions;
using Cockpit.App.ViewModels;
using Cockpit.TestSupport;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Keeps AC-999's two categories honest. The Options dialog now promises that Cancel puts every *value* back and
/// that no *action* is taken back, and the difference between the two is a judgement somebody makes per control.
/// These read the dialog itself and fail when a control was added without that judgement being made — which is the
/// failure worth catching, because both wrong answers are silent: a value left out of the buffer is a change that
/// survives a Cancel, and an action folded into it is a Cancel that promises what it cannot deliver.
/// </summary>
public class OptionsStagingGuardTests
{
    private static readonly string DialogMarkup =
        File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", "Cockpit.App", "Views", "OptionsDialog.axaml"));

    private static readonly string DialogCodeBehind =
        File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", "Cockpit.App", "Views", "OptionsDialog.axaml.cs"));

    [Fact]
    public void EveryEditableControlInTheDialog_IsEitherStagedOrDeclaredImmediate()
    {
        var known = OptionsStaging.EditedProperties.Concat(OptionsStaging.ImmediateOrTransient).ToHashSet(StringComparer.Ordinal);

        var unclassified = _EditableBindings()
            // A path that does not resolve on this view model belongs to a nested DataContext — the Debug tab
            // shows a remote cockpit's own panel — and is that panel's business, not this transaction's.
            .Where(binding => _Resolves(typeof(CockpitViewModel), binding))
            .Where(binding => !known.Contains(binding))
            .ToList();

        Assert.True(
            unclassified.Count == 0,
            "OptionsDialog.axaml binds an editable control to "
            + string.Join(", ", unclassified)
            + ", which is in neither OptionsStaging.EditedProperties (a value the dialog buffers and Cancel puts "
            + "back) nor OptionsStaging.ImmediateOrTransient (not a setting). Add it to one of them on purpose.");
    }

    [Fact]
    public void NothingIsClassifiedTwice()
    {
        Assert.Empty(OptionsStaging.EditedProperties.Intersect(OptionsStaging.ImmediateOrTransient, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryStagedPropertyPath_StillResolvesOnTheViewModel()
    {
        var missing = OptionsStaging.EditedProperties
            .Where(path => !_Resolves(typeof(CockpitViewModel), path))
            .ToList();

        Assert.True(missing.Count == 0, "renamed or removed since the list was written: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryClickHandlerInTheDialog_IsADeclaredImmediateAction_OrOneOfTheFooterButtons()
    {
        // The footer's own three are the transaction, not actions inside it.
        string[] footer = ["OnApplyAndClose", "OnCancel"];
        var known = OptionsStaging.ImmediateActionHandlers.Concat(footer).ToHashSet(StringComparer.Ordinal);

        var unclassified = Regex.Matches(DialogMarkup, @"Click=""(?<handler>\w+)""")
            .Select(match => match.Groups["handler"].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(handler => !known.Contains(handler))
            .ToList();

        Assert.True(
            unclassified.Count == 0,
            "OptionsDialog.axaml wires " + string.Join(", ", unclassified)
            + " to a button. If it acts on the spot, name it in OptionsStaging.ImmediateActionHandlers so Cancel is "
            + "not read as undoing it; if it edits a value, it belongs on a bound control instead.");
    }

    [Fact]
    public void EveryDeclaredImmediateAction_StillExists()
    {
        var missing = OptionsStaging.ImmediateActionHandlers
            .Where(handler => !DialogCodeBehind.Contains($"void {handler}(", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0, "declared immediate but gone from the code-behind: " + string.Join(", ", missing));
    }

    // The bindings an operator can change: the value-carrying property on each input control, with item templates
    // stripped first — those bind to a row, not to the dialog's own view model.
    private static IEnumerable<string> _EditableBindings()
    {
        var markup = Regex.Replace(
            DialogMarkup,
            @"<(DataTemplate|\w+\.ItemTemplate)\b[^>]*>.*?</\1>",
            string.Empty,
            RegexOptions.Singleline);

        // The debug tab hosts a remote cockpit's own panel, whose controls bind to that panel and not to this
        // dialog's view model.
        markup = Regex.Replace(markup, @"<ItemsControl\b.*?</ItemsControl>", string.Empty, RegexOptions.Singleline);

        Dictionary<string, string[]> editable = new(StringComparer.Ordinal)
        {
            ["CheckBox"] = ["IsChecked"],
            ["ToggleSwitch"] = ["IsChecked"],
            ["ToggleButton"] = ["IsChecked"],
            ["RadioButton"] = ["IsChecked"],
            ["TextBox"] = ["Text"],
            ["NumericUpDown"] = ["Value"],
            ["Slider"] = ["Value"],
            ["ComboBox"] = ["SelectedItem", "SelectedValue"],
            ["AutoCompleteBox"] = ["Text", "SelectedItem"],
        };

        foreach (Match element in Regex.Matches(markup, @"<(?<tag>[A-Za-z][\w.:]*)\b(?<attributes>(?:[^<>""]|""[^""]*"")*?)/?>"))
        {
            var tag = element.Groups["tag"].Value.Split(':')[^1];
            if (!editable.TryGetValue(tag, out var properties)
                // A read-only box is a place to read a value off, not to set one.
                || element.Groups["attributes"].Value.Contains(@"IsReadOnly=""True""", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var property in properties)
            {
                var binding = Regex.Match(
                    element.Groups["attributes"].Value,
                    property + @"=""\{(?:CompiledBinding|Binding)\s+(?<path>[^},]+)");

                if (binding.Success)
                {
                    yield return binding.Groups["path"].Value.Trim();
                }
            }
        }
    }

    private static bool _Resolves(Type type, string path)
    {
        Type? current = type;
        foreach (var segment in path.Split('.'))
        {
            var property = current?.GetProperty(segment);
            if (property is null)
            {
                return false;
            }

            current = property.PropertyType;
        }

        return true;
    }
}
