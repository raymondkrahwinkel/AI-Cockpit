using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Every <c>/template/ Type#Name</c> selector in <c>Theme.axaml</c> has to name a part that the control's template
/// actually builds. A selector that names one it does not is not an error anywhere — it compiles, it loads, and it
/// silently styles nothing, so the control keeps whatever the Fluent theme gave it and no one finds out until
/// someone looks at the screen.
/// <para>
/// This is not hypothetical. <c>TextBox /template/ TextBlock#PART_Watermark</c> sat in the theme through several
/// releases: Avalonia 12 calls that part <c>PART_Placeholder</c>, so every placeholder in the app was drawing in
/// Fluent's grey rather than the theme's faint text, and the rule that was supposed to fix it had been dead the
/// whole time (AC-336).
/// </para>
/// <para>
/// It reads the source rather than the loaded style list because a selector's part name is not recoverable from a
/// compiled style — matching is what Avalonia does with it, and a match that never happens leaves no trace.
/// </para>
/// </summary>
[Collection("avalonia")]
public partial class ThemeTemplatePartTests
{
    /// <summary>
    /// One subject per selector path the theme reaches into, keyed by that path with its pseudo-classes stripped.
    /// Style classes are part of the key because a class can bring its own template — <c>CheckBox.Switch</c> is a
    /// track and a knob where a plain CheckBox is a box and a tick, so asking the plain one about a part named
    /// Track answers a question nobody asked.
    /// <para>
    /// A subject is (what goes in the window, how to find the control being styled): for a nested path like
    /// <c>ListBox.subnav ListBoxItem</c> the item only gets that template while it is inside that ListBox.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, Func<(Control Host, Func<Control, Control> Subject)>> Subjects = new()
    {
        ["CheckBox"] = () => (new CheckBox { Content = "x", IsChecked = true }, host => host),
        ["CheckBox.Switch"] = () =>
            (new CheckBox { Classes = { "Switch" }, Content = "x", IsChecked = true }, host => host),
        ["ComboBox"] = () => (new ComboBox { ItemsSource = new[] { "a", "b" }, SelectedIndex = 0 }, host => host),
        ["RadioButton"] = () => (new RadioButton { Content = "x", IsChecked = true }, host => host),
        ["TextBox"] = () => (new TextBox { Text = "x" }, host => host),
        ["ListBox.subnav ListBoxItem"] = () => (
            new ListBox { Classes = { "subnav" }, ItemsSource = new[] { "a" }, SelectedIndex = 0 },
            host => host.GetVisualDescendants().OfType<ListBoxItem>().First()),
    };

    [Fact]
    public void EverySelectorNamingATemplatePart_NamesOneThatExists()
    {
        var selectors = _NamedPartSelectors();

        // The theme reaches into templates; a run that finds none is a broken parser, not a clean theme.
        Assert.NotEmpty(selectors);

        var missing = new List<string>();
        HeadlessAvalonia.Run(() =>
        {
            foreach (var (path, part, selector) in selectors)
            {
                if (!Subjects.TryGetValue(path, out var build))
                {
                    // A path this test cannot build says nothing about the selector — but it must be visible,
                    // otherwise the exclusion becomes the place a dead selector hides.
                    missing.Add($"{selector} — no subject registered for '{path}'; add one to {nameof(Subjects)}");
                    continue;
                }

                if (!_PartNames(build()).Contains(part))
                {
                    missing.Add($"{selector} — '{path}' has no template part named '{part}'");
                }
            }
        });

        Assert.True(missing.Count == 0,
            "a selector naming a part that does not exist styles nothing, and says so to no one:"
            + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// The parser is the load-bearing half: a regex that quietly stops matching would turn this guard green while
    /// the theme fills up with dead selectors. This holds it to a shape it has to keep finding.
    /// </summary>
    [Fact]
    public void TheParser_FindsAPartSelectorAmongOrdinaryOnes()
    {
        var found = _ParseSelectors(
            """
            <Style Selector="Button:pointerover /template/ ContentPresenter"/>
            <Style Selector="TextBox:focus /template/ Border#PART_BorderElement"/>
            <Style Selector="Border.tag > :is(TextBlock)"/>
            <Style Selector="ListBox.subnav ListBoxItem:selected /template/ Border#ItemBg"/>
            <Style Selector="NumericUpDown /template/ ButtonSpinner /template/ RepeatButton#PART_Spinner"/>
            """).ToList();

        // Only the selectors naming a part by # are this guard's business.
        Assert.Equal(3, found.Count);
        // A pseudo-class says when a rule applies, so it is not part of the subject.
        Assert.Contains(found, hit => hit.Path == "TextBox" && hit.Part == "PART_BorderElement");
        // The scoping ancestor stays in the path — it is what gives the item that template.
        Assert.Contains(found, hit => hit.Path == "ListBox.subnav ListBoxItem" && hit.Part == "ItemBg");
        // Both hops of a nested template are found, not just the first.
        Assert.Contains(found, hit => hit.Part == "PART_Spinner");
    }

    /// <summary>Names of every element in the styled control's own template, after it has been applied and laid out.</summary>
    private static HashSet<string> _PartNames((Control Host, Func<Control, Control> Subject) built)
    {
        var window = new Window { Width = 400, Height = 300, Content = built.Host };
        window.Show();
        window.UpdateLayout();

        var names = built.Subject(built.Host).GetVisualDescendants()
            .OfType<StyledElement>()
            .Select(element => element.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

        window.Close();

        return names;
    }

    private static IReadOnlyList<(string Path, string Part, string Selector)> _NamedPartSelectors()
    {
        var theme = _LocateThemeFile();
        Assert.True(theme is not null, "the guard reads Theme.axaml from the source tree");

        return _ParseSelectors(File.ReadAllText(theme!)).ToList();
    }

    private static IEnumerable<(string Path, string Part, string Selector)> _ParseSelectors(string markup)
    {
        foreach (Match match in SelectorAttribute().Matches(markup))
        {
            var selector = match.Groups["selector"].Value;
            // Matches, not Match: a selector can hop through two templates
            // ("NumericUpDown /template/ ButtonSpinner /template/ RepeatButton#x"), and taking only the first
            // named part would let the second one rot unseen — the exact failure this guard exists to catch.
            foreach (Match part in NamedPart().Matches(selector))
            {
                // Everything left of the *first* /template/ decides which control is styled and which template it
                // is wearing: "ListBox.subnav ListBoxItem:selected" styles parts of the item, and only while it
                // sits in that rail. Pseudo-classes drop out — they say when a rule applies, not what it applies to.
                var path = PseudoClass().Replace(
                    selector[..selector.IndexOf("/template/", StringComparison.Ordinal)], string.Empty);

                yield return (string.Join(' ', path.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
                    part.Groups["part"].Value, selector);
            }
        }
    }

    private static string? _LocateThemeFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Cockpit.App", "Styles", "Theme.axaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    [GeneratedRegex("Selector=\"(?<selector>[^\"]+)\"")]
    private static partial Regex SelectorAttribute();

    [GeneratedRegex(@"/template/\s+\w+#(?<part>[\w]+)")]
    private static partial Regex NamedPart();

    /// <summary>A pseudo-class such as <c>:selected</c> or <c>:focus-visible</c>.</summary>
    [GeneratedRegex(@":[\w-]+")]
    private static partial Regex PseudoClass();
}
