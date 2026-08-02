using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Configuration;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The window shell every dialog wears (AC-335). Two things had drifted: two dialogs never got the cockpit's own
/// title bar and showed the OS one instead, and nine drew their name a second time inside their content — so the
/// same word appeared twice, at two sizes, one above the other. These measure the real windows.
/// </summary>
[Collection("avalonia")]
public class DialogChromeTests
{
    // The dialogs that take their name from a view model apply the chrome once they have one, so a bare
    // construction is not the moment to ask them. They are covered by TheyWearItOnceTheirViewModelArrives below.
    private static readonly HashSet<string> TitledByTheirViewModel =
    [
        nameof(ConfirmationDialog),
        nameof(MemorySourceLocationPickerDialog),
        nameof(NewSessionDialog),
        nameof(PasswordDialog),
        nameof(ProjectDialog),
        nameof(SetStatusDialog),
        nameof(SharedProjectBindingDialog),
    ];

    public static TheoryData<Type> DialogTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in _DialogWindowTypes().Where(type => !TitledByTheirViewModel.Contains(type.Name)))
        {
            data.Add(type);
        }

        return data;
    }

    [Fact]
    public void TheDialogsUnderTest_AreNotAnEmptySet()
    {
        // xunit runs a theory whose data source yields nothing as zero tests and calls the run green, so a
        // reflection query that quietly stops matching would take this whole file's coverage with it silently.
        Assert.True(DialogTypes().Count > 10, $"only {DialogTypes().Count} dialog windows were found to check");
    }

    [Theory]
    [MemberData(nameof(DialogTypes))]
    public void EveryDialog_WearsTheCockpitTitleBar(Type dialogType) => HeadlessAvalonia.Run(() =>
    {
        var window = (Window)Activator.CreateInstance(dialogType)!;

        // BorderOnly is what the chrome swaps the OS caption for; a dialog that never called it keeps the
        // platform default and so shows two different title bars in one app.
        Assert.Equal(WindowDecorations.BorderOnly, window.WindowDecorations);
    });

    // One entry per dialog excluded above, given the view model it waits for.
    private static readonly Dictionary<string, Func<Window>> DeferredDialogs = new()
    {
        [nameof(ConfirmationDialog)] = () => new ConfirmationDialog { DataContext = new ConfirmationDialogViewModel() },
        [nameof(MemorySourceLocationPickerDialog)] = () => new MemorySourceLocationPickerDialog { DataContext = new MemorySourceLocationPickerViewModel() },
        [nameof(NewSessionDialog)] = () => new NewSessionDialog { DataContext = new NewSessionDialogViewModel() },
        [nameof(PasswordDialog)] = () => new PasswordDialog { DataContext = new PasswordDialogViewModel() },
        [nameof(ProjectDialog)] = () => new ProjectDialog { DataContext = new ProjectDialogViewModel() },
        [nameof(SetStatusDialog)] = () => new SetStatusDialog { DataContext = new SetStatusDialogViewModel("AC-335") },
        [nameof(SharedProjectBindingDialog)] = () => new SharedProjectBindingDialog { DataContext = new SharedProjectBindingDialogViewModel() },
    };

    public static TheoryData<string> DeferredDialogNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in DeferredDialogs.Keys.Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DeferredDialogNames))]
    public void TheyWearItOnceTheirViewModelArrives(string name) => HeadlessAvalonia.Run(() =>
        Assert.Equal(WindowDecorations.BorderOnly, DeferredDialogs[name]().WindowDecorations));

    [Fact]
    public void TheExcludedDialogs_AreExactlyTheOnesCheckedWithAViewModel()
    {
        // Otherwise the exclusion list becomes the place a dialog goes to stop being checked at all: excluded
        // from the bare-construction theory, and never given a view model anywhere either.
        Assert.Equal(TitledByTheirViewModel.Order(StringComparer.Ordinal), DeferredDialogs.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ANameThatComesFromTheViewModel_IsOnTheBarRatherThanAvaloniasDefault() => HeadlessAvalonia.Run(() =>
    {
        // The New-session dialog binds its title, and a binding has not run while the constructor has. Applying
        // the chrome there left the bar reading Avalonia's own default name for an untitled window.
        var viewModel = new NewSessionDialogViewModel();
        var window = new NewSessionDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        Assert.DoesNotContain(_VisibleTextBlocks(window), block => block.Text == "Window");
        Assert.Contains(_VisibleTextBlocks(window), block => block.Text == viewModel.HeaderText);
    });

    [Fact]
    public void ADialog_StatesItsNameOnce() => HeadlessAvalonia.Run(() =>
    {
        var window = _ShownProjectsDialog();

        Assert.Single(_VisibleTextBlocks(window), block => block.Text == "Projects");
    });

    [Fact]
    public void ADialogsName_IsAtHeadingScaleWithItsLineUnderneath() => HeadlessAvalonia.Run(() =>
    {
        var window = _ShownProjectsDialog();

        Assert.Equal(15d, _Name(window).FontSize);

        var subtitle = Assert.Single(_VisibleTextBlocks(window),
            block => block.Text?.StartsWith("What your sessions work on", StringComparison.Ordinal) == true);
        Assert.Equal(11.5d, subtitle.FontSize);
        Assert.True(subtitle.Bounds.Height > 0, "a heading's second line has to be on screen to be a subtitle");

        // The line under the name explains it, so it may not read as loudly as the name. Size alone does not say
        // that: weight and colour are what make text loud, and a smaller line set in bold at full strength reads
        // louder than the name above it. All three, or the sentence is a wish.
        Assert.True(subtitle.FontSize < _Name(window).FontSize, "the explanation is set larger than the name");
        Assert.True(subtitle.FontWeight <= _Name(window).FontWeight, "the explanation is set heavier than the name");
        Application.Current!.TryFindResource("CockpitTextFaintBrush", out var faint);
        Assert.Same(faint, subtitle.Foreground);
    });

    // The room the chrome puts around a dialog's heading, as AC-426 set it: 20 either side, 12 above and below,
    // the seam under the band, and a single pixel between the name and the line explaining it.
    private const double SidePadding = 20;
    private const double VerticalPadding = 12;
    private const double Seam = 1;
    private const double LineSpacing = 1;
    private const double CaptionButtonHeight = 26;

    // What the bar comes to at those values, measured on the window this theory builds. The old figures on the
    // same rig were 64 and 83 — the 63 and 97 quoted below are real dialogs, so the two sets are not each other's
    // before and after. A change here is either the header growing back or the bundled font moving under us; both
    // are worth being stopped for.
    private const double BarWithAName = 44;
    private const double BarWithANameAndALine = 59;

    [Theory]
    [InlineData(null, BarWithAName)]
    [InlineData("What is this session working on?", BarWithANameAndALine)]
    public void TheChrome_GivesItsHeadingExactlyTheRoomThisTicketChose(string? subtitle, double barHeight) => HeadlessAvalonia.Run(() =>
    {
        // AC-426: the bar ran to 63px for a name alone and 97px with its explanation — on a short dialog like Set
        // status, two fifths of the window before its first control. The values that brought it down are held here
        // one by one, against the geometry the chrome actually lays out.
        //
        // Each on its own rather than through a derived total. A total is the cheaper test and the worse one:
        // earlier versions of this guard held a single difference, and each let something they were meant to watch
        // move freely underneath — the line spacing, then the horizontal padding. Sums hide their own terms.
        //
        // The bar's own height leads, because it is the thing the ticket was about and the only figure no relative
        // measurement can hold: anything that inflates a line inflates the heading with it, so every difference
        // below stays true while the bar grows. It is an exact number and safe to be one — the harness bundles
        // Inter (HeadlessAvalonia), so the text metric is not the machine's, and CI measures the same figure.
        var window = new Window { Width = 600, Height = 400 };
        CockpitWindowChrome.Apply(window, "Set status", subtitle);
        window.Show();
        window.UpdateLayout();

        var band = _Band(window);
        var heading = _Heading(band);
        var lines = heading.Children.OfType<TextBlock>().ToList();
        var captionColumn = _CaptionColumn(band);

        Assert.Equal(barHeight, band.Bounds.Height, 1);
        Assert.Equal(SidePadding, heading.Bounds.Left, 1);
        Assert.Equal(SidePadding, captionColumn.Bounds.Left - heading.Bounds.Right, 1);
        Assert.Equal(VerticalPadding, heading.Bounds.Top, 1);
        Assert.Equal(VerticalPadding + Seam, band.Bounds.Height - heading.Bounds.Bottom, 1);
        Assert.Equal(LineSpacing * (lines.Count - 1), heading.Bounds.Height - lines.Sum(line => line.Bounds.Height), 1);
        Assert.Equal(CaptionButtonHeight, _CloseButton(band).Bounds.Height, 1);
    });

    [Fact]
    public void TheSameHoldsOnARealDialog_NotOnlyOnABareWindowWearingTheChrome() => HeadlessAvalonia.Run(() =>
    {
        // The measurement above is taken on a window that is nothing but the chrome. A technique that only works
        // there would be measuring its own test rig, so the same figures are asked of a dialog with a full body —
        // including a footer standing on the very same band colour.
        var band = _Band(_ShownProjectsDialog());
        var heading = _Heading(band);

        // The bar's own height first: it is what this whole guard is built around, and proving it only on the rig
        // would leave the one figure that matters resting on the rig being representative.
        Assert.Equal(BarWithANameAndALine, band.Bounds.Height, 1);
        Assert.Equal(SidePadding, heading.Bounds.Left, 1);
        Assert.Equal(VerticalPadding, heading.Bounds.Top, 1);
        Assert.Equal(VerticalPadding + Seam, band.Bounds.Height - heading.Bounds.Bottom, 1);
    });

    [Fact]
    public void TheSubtitle_RunsToItsBoundAndStopsThere() => HeadlessAvalonia.Run(() =>
    {
        // The sister of ATitleFullOfNewlines_DoesNotGrowTheBar. SubtitleMaxLines calls itself "bounded rather than
        // trusted" and nothing held it to that. Held from both ends, and from below by the line just under the
        // bound rather than by any shorter one: a lower end that only rules out a single line still lets the bound
        // slip from three to two, losing an explanation's last line mid-word with the suite green.
        var justUnder = _BarHeight("Settings", "one\ntwo");
        var atTheBound = _BarHeight("Settings", "one\ntwo\nthree");
        var wellPast = _BarHeight("Settings", string.Join("\n", Enumerable.Repeat("padding", 40)));

        Assert.True(atTheBound > justUnder, $"a three-line explanation is no taller than a two-line one ({atTheBound})");
        Assert.Equal(atTheBound, wellPast, 1);
    });

    [Fact]
    public void TheCloseButton_HangsFromTheTopOfTheHeading_RatherThanTheEdgeOfTheBar() => HeadlessAvalonia.Run(() =>
    {
        // The caption column carries the same top padding as the heading, so the ✕ lines up with the name instead
        // of floating against the bar's edge — the reference's align-items: flex-start. Dropping that margin costs
        // nothing any height measurement would notice.
        var window = new Window { Width = 600, Height = 400 };
        CockpitWindowChrome.Apply(window, "Set status", "What is this session working on?");
        window.Show();
        window.UpdateLayout();

        // The column and the heading are siblings in the bar, so their Bounds share an origin; the button's own
        // Bounds are relative to the column that carries the margin, and would read 0 either way.
        var band = _Band(window);

        Assert.Equal(_Heading(band).Bounds.Top, _CaptionColumn(band).Bounds.Top, 1);
    });

    // The title bar, which a dialog's footer is indistinguishable from by colour alone — it stands on the same
    // band, the same height, with the seam on its other edge. Document order is what separates them, and the
    // chrome docks the title bar before the body, so the first is the one wanted. Should that ever stop holding,
    // _Heading throws on the footer rather than quietly measuring it: the footer carries buttons, not text.
    private static Border _Band(Window window) =>
        window.GetVisualDescendants().OfType<Border>().First(border =>
            border.Background is ISolidColorBrush brush && brush.Color == _Colour("CockpitChromeBgColor"));

    // Close is added after the gear, minimize and maximize, so it is last under every combination of them.
    private static Button _CloseButton(Border band) => band.GetVisualDescendants().OfType<Button>().Last();

    private static StackPanel _CaptionColumn(Border band) =>
        band.GetVisualDescendants().OfType<StackPanel>().First(panel => panel.Children.OfType<Button>().Any());

    // The heading is the one panel in the band whose own children are the text; the caption column's children are
    // buttons, so a glyph inside a button cannot be counted as a line of the heading.
    private static StackPanel _Heading(Border band) =>
        band.GetVisualDescendants().OfType<StackPanel>().First(panel => panel.Children.OfType<TextBlock>().Any());

    [Fact]
    public void TheAppWindowsBar_NamesTheProductOnce_OnOneCompactLine() => HeadlessAvalonia.Run(() =>
    {
        // The app window itself needs the running app's services to build its panes, so the bar is measured on a
        // bare window given the same chrome — which is the whole point of the chrome being shared.
        var window = new Window { Width = 600, Height = 200 };
        CockpitWindowChrome.Apply(window, "a title from the caller", titleBar: CockpitTitleBar.Window);
        window.Show();
        window.UpdateLayout();

        // The app's own window is not named by whoever applied the chrome: it carries the product's name (AC-430),
        // and it carries it once — the bar is the single place in the main window the name is stated.
        var name = Assert.Single(_VisibleTextBlocks(window), block => block.Inlines is { Count: > 0 });
        var runs = name.Inlines!.Cast<Run>().ToList();
        Assert.Equal(CockpitProduct.DisplayName, string.Concat(runs.Select(run => run.Text)));
        Assert.DoesNotContain(_VisibleTextBlocks(window), block => block.Text == "a title from the caller");

        // One line, with no room asked for an explanation under it: what matters on the app window is the cockpit
        // below the bar. It sits a half-point above a dialog's heading since AC-426 took the weight out of that
        // one — the window that names the product is the one place the name is allowed to lead.
        Assert.Equal(15.5d, name.FontSize);

        // The product's half steps back and the maker's half does not — the mockup's `Wispslate <span>Cockpit</span>`.
        // The first run sets no colour of its own and inherits the bar's, which is the point: only one of the two
        // is tinted, so asking whether the second is the faint brush and the first is not says exactly that.
        Application.Current!.TryFindResource("CockpitTextFaintBrush", out var faint);
        Assert.Same(faint, runs[1].Foreground);
        Assert.NotSame(faint, runs[0].Foreground);
    });

    [Fact]
    public void TheAppWindowsBar_CarriesTheMark_WithoutDeformingIt() => HeadlessAvalonia.Run(() =>
    {
        var window = new Window { Width = 600, Height = 200 };
        CockpitWindowChrome.Apply(window, titleBar: CockpitTitleBar.Window);
        window.Show();
        window.UpdateLayout();

        // The mark is wider than it is tall (AC-430 acceptance 4: no stretched icon anywhere). Only its height is
        // set, so the width the layout gives it has to follow the bitmap's own aspect rather than a square.
        var mark = Assert.Single(window.GetVisualDescendants().OfType<Image>());
        Assert.NotNull(mark.Source);
        var aspect = mark.Source!.Size.Width / mark.Source.Size.Height;
        Assert.Equal(mark.Bounds.Height * aspect, mark.Bounds.Width, 1);
    });

    [Fact]
    public void ATitleFullOfNewlines_DoesNotGrowTheBar() => HeadlessAvalonia.Run(() =>
    {
        // A plugin supplies the title on its own dialog (PluginDialogHost), so a title is not always the app's
        // own string. The bar used to be a fixed 38px, which bounded a hostile one by accident; it now grows
        // with its heading, so the heading is what has to hold the line.
        var plain = _BarHeight("Settings");
        var hostile = _BarHeight("Settings\n" + string.Join("\n", Enumerable.Repeat("padding", 40)));

        Assert.Equal(plain, hostile);
    });

    private static double _BarHeight(string title, string? subtitle = null)
    {
        var window = new Window { Width = 600, Height = 400 };
        CockpitWindowChrome.Apply(window, title, subtitle);
        window.Show();
        window.UpdateLayout();

        return _Band(window).Bounds.Height;
    }

    [Fact]
    public void TheTitleBar_SitsOnTheChromeBand() => HeadlessAvalonia.Run(() =>
    {
        var window = _ShownProjectsDialog();
        var band = Assert.Single(_Name(window).GetVisualAncestors().OfType<Border>(), border => border.Background is not null);

        var fill = Assert.IsAssignableFrom<ISolidColorBrush>(band.Background);
        var seam = Assert.IsAssignableFrom<ISolidColorBrush>(band.BorderBrush);

        Assert.Equal(_Colour("CockpitChromeBgColor"), fill.Color);
        Assert.Equal(_Colour("CockpitHairlineSoftColor"), seam.Color);
    });

    private static ProjectsDialog _ShownProjectsDialog()
    {
        var window = new ProjectsDialog { DataContext = ProjectsViewModel.DesignSample() };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static TextBlock _Name(ProjectsDialog window) =>
        Assert.Single(_VisibleTextBlocks(window), block => block.Text == "Projects");

    private static Color _Colour(string key) =>
        Avalonia.Application.Current?.TryFindResource(key, out var value) == true && value is Color colour
            ? colour
            : throw new InvalidOperationException($"The theme has no '{key}' — the dialog chrome is built on it.");

    private static List<TextBlock> _VisibleTextBlocks(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsVisible).ToList();

    // Every dialog window the app ships, found the way a new one would arrive: by being a Window under Views whose
    // name ends in Dialog. A dialog added without the shared chrome fails EveryDialog_WearsTheCockpitTitleBar
    // rather than being noticed the next time someone opens it.
    private static IEnumerable<Type> _DialogWindowTypes() =>
        typeof(ProjectsDialog).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, Namespace: "Cockpit.App.Views" }
                && type.Name.EndsWith("Dialog", StringComparison.Ordinal)
                && type.IsSubclassOf(typeof(Window))
                && type.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(type => type.Name, StringComparer.Ordinal);
}
