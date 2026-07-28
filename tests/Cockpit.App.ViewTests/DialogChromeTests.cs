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
        nameof(NewSessionDialog),
        nameof(PasswordDialog),
        nameof(ProjectDialog),
        nameof(SetStatusDialog),
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
        [nameof(NewSessionDialog)] = () => new NewSessionDialog { DataContext = new NewSessionDialogViewModel() },
        [nameof(PasswordDialog)] = () => new PasswordDialog { DataContext = new PasswordDialogViewModel() },
        [nameof(ProjectDialog)] = () => new ProjectDialog { DataContext = new ProjectDialogViewModel() },
        [nameof(SetStatusDialog)] = () => new SetStatusDialog { DataContext = new SetStatusDialogViewModel("AC-335") },
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

        Assert.Equal(20d, _Name(window).FontSize);

        var subtitle = Assert.Single(_VisibleTextBlocks(window),
            block => block.Text?.StartsWith("What your sessions work on", StringComparison.Ordinal) == true);
        Assert.Equal(12.5d, subtitle.FontSize);
        Assert.True(subtitle.Bounds.Height > 0, "a heading's second line has to be on screen to be a subtitle");
    });

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

        // Smaller than a dialog's heading, and with no room asked for an explanation under it: what matters on the
        // app window is the cockpit below the bar.
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

    private static double _BarHeight(string title)
    {
        var window = new Window { Width = 600, Height = 400 };
        CockpitWindowChrome.Apply(window, title);
        window.Show();
        window.UpdateLayout();

        var name = window.GetVisualDescendants().OfType<TextBlock>().First(block => block.IsVisible);
        return name.GetVisualAncestors().OfType<Border>().First().Bounds.Height;
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
