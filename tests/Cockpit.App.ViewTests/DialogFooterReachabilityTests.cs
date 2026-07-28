using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Xunit.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Every dialog keeps its buttons inside its own window, whatever its content says (AC-428). AC-427 found one
/// dialog where they did not — the MCP servers window, where an operator could configure a server and then not
/// reach Save — and the sweep this guards is the answer to "how many others".
/// <para>
/// The mechanism is horizontal, not the height problem it looked like: an element whose width comes from data
/// sharing a track with fixed buttons. A <c>Grid</c> does not clip and a <c>StackPanel</c> does not either, so
/// the buttons are laid out past the window's edge and the window cuts them off. That is what the stress below
/// reproduces: every piece of text outside a scroller is given a long value, because a layout that only holds
/// while a string happens to be short is the defect, not the absence of one.
/// </para>
/// <para>
/// The scenes come from <see cref="Screenshotter.SceneNames"/> — the table the app renders from and the theme
/// baseline (AC-338) already reads — so a dialog added later is covered by having a scene at all, and
/// <see cref="EveryDialogWindow_IsBuiltByAScene"/> is what makes having one non-optional.
/// </para>
/// <para>
/// <b>Dialogs only, deliberately.</b> The same shape exists in the main window's status bar, where a long
/// session status pushes an icon past the right edge. It is out of this ticket's scope and recorded rather
/// than quietly folded in.
/// </para>
/// </summary>
[Collection("avalonia")]
public class DialogFooterReachabilityTests(ITestOutputHelper output)
{
    // Long enough to overrun the widest dialog here (1200) several times over, and a real sentence rather than
    // a run of Xs so it wraps and measures the way the app's own status messages do.
    private const string LongEnoughToOverrun =
        "Hidden here because the cockpit already runs a server by that name: filesystem, fetch, git, ripgrep, " +
        "sequential-thinking, memory and time. Saving removes them — rename yours first if you meant to keep it.";

    /// <summary>A 13-inch laptop panel, the size the ticket names: the dialogs declaring 680–760px bite here.</summary>
    private const int LaptopWidth = 1366;
    private const int LaptopHeight = 768;

    /// <summary>How much of a dialog's foot counts as its footer: two rows of buttons with their padding.</summary>
    private const double FooterBand = 120;

    /// <summary>
    /// Shorter than the shortest dialog here, so the cap bites on every one of them rather than only the tall
    /// ones. A dialog that declares a MinHeight is squeezed to that instead — that is the smallest the clamp
    /// will ever make it, because the clamp never goes below a window's own minimum.
    /// </summary>
    private const double SqueezedHeight = 200;

    public static TheoryData<string> Scenes => [.. Screenshotter.SceneNames];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void ADialogKeepsItsButtonsInsideItself_WhateverItsTextSays(string scene) => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene(scene, LaptopWidth, LaptopHeight);
        try
        {
            window.UpdateLayout();
            if (!_IsDialog(window))
            {
                return;
            }

            var stressed = _StressTheFooterBand(window);
            window.UpdateLayout();

            var unreachable = _Unreachable(window);
            Assert.True(unreachable.Count == 0,
                $"'{scene}' put {unreachable.Count} button(s) outside a {window.Bounds.Width:0.#}×" +
                $"{window.Bounds.Height:0.#} window after {stressed} of its texts were given a long value:" +
                Environment.NewLine + string.Join(Environment.NewLine, unreachable));
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// The other axis, and the reason it is a ceiling rather than a size: a dialog is centred on its owner with
    /// nothing to drag it back by, so one that opens taller than the screen has put its own buttons past the
    /// bottom edge. <see cref="Cockpit.App.Controls.DialogScreenClamp"/> now runs for every dialog through the
    /// shared chrome; this asserts it reached this one, because three of the twenty-one used to ask for it by
    /// hand and the other eighteen simply did not.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scenes))]
    public void ADialogIsBoundedByTheScreenItOpensOn(string scene) => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene(scene, LaptopWidth, LaptopHeight);
        try
        {
            window.UpdateLayout();
            if (!_IsDialog(window))
            {
                return;
            }

            var screen = window.Screens.ScreenFromWindow(window);
            Assert.NotNull(screen);

            var available = screen.WorkingArea.Height / screen.Scaling;
            output.WriteLine($"{scene}: maxHeight={window.MaxHeight:0.#} of a {available:0.#} working area");

            Assert.True(window.MaxHeight <= available,
                $"'{scene}' may grow to {window.MaxHeight:0.#} on a screen offering {available:0.#}");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// What the cap above is for. A ceiling only helps if the dialog under it gives way in the right place: the
    /// part that scrolls, never the row of buttons. Squeezed to the smallest height it claims to work at — which
    /// is what a small screen does to it — the buttons still have to be inside the window.
    /// <para>
    /// This is the ticket's own mutation, run over every dialog rather than one: shrink the height below the
    /// content and see what leaves. It is how the About and plugin-consent dialogs were caught, whose buttons
    /// were the last children of the panel that sized the window, so capping the window removed them.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Scenes))]
    public void ADialogSqueezedBelowItsContent_StillHasItsButtons(string scene) => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.BuildScene(scene, LaptopWidth, LaptopHeight);
        if (!_IsDialog(window))
        {
            return;
        }

        // Set before it is shown: a headless window keeps the size it opened at, so capping it afterwards
        // measures the old layout against a smaller number and reports a failure that is the test's own.
        window.MaxHeight = Math.Max(window.MinHeight, SqueezedHeight);
        window.Height = window.MaxHeight;
        window.Show();
        try
        {
            window.UpdateLayout();

            var unreachable = _Unreachable(window);
            Assert.True(unreachable.Count == 0,
                $"'{scene}' at {window.Bounds.Height:0.#} high — the smallest it claims to work at — left " +
                $"{unreachable.Count} button(s) outside itself:" +
                Environment.NewLine + string.Join(Environment.NewLine, unreachable));
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// The one button label in the app that comes from a caller rather than from markup: plugins reach this
    /// dialog through <c>ICockpitActions.ConfirmAsync</c>, whose third argument is a free string. The window is
    /// fixed at 440 and cannot be resized, so an unbounded label laid the button out past the right edge —
    /// measured at x=634 for an 86-character one.
    /// </summary>
    [Fact]
    public void AConfirmLabelFromAPlugin_CannotPushItsOwnButtonOffTheDialog() => HeadlessAvalonia.Run(() =>
    {
        var window = new Views.ConfirmationDialog
        {
            DataContext = new ViewModels.ConfirmationDialogViewModel(
                "Remove the worktree",
                "This removes the worktree and everything in it that was never committed.",
                "Yes, delete the worktree, the branch it is on, and every file that was never committed"),
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            var unreachable = _Unreachable(window);

            Assert.True(unreachable.Count == 0,
                "a plugin's confirm label must not carry its own button off the dialog:" +
                Environment.NewLine + string.Join(Environment.NewLine, unreachable));
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// The second band in the profiles dialog's footer, which replaces the first one while a removal is being
    /// confirmed. A scene shows a dialog in one state, so the theories above never see this one — a mutation of
    /// its columns stayed green, which is how that was found. Its question carries a profile label the operator
    /// typed, so it is the same shape as the status line beside it.
    /// </summary>
    [Fact]
    public void TheRemoveConfirmation_KeepsItsAnswersInsideTheDialog() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ViewModels.ManageProfilesDialogViewModel
        {
            PendingRemovalLabel = "the profile I keep for the long-running review sessions on the big repository",
            IsConfirmingRemove = true,
        };

        var window = new Views.ManageProfilesDialog { DataContext = viewModel };
        window.Show();
        try
        {
            window.UpdateLayout();
            var unreachable = _Unreachable(window);

            Assert.True(unreachable.Count == 0,
                "the answer to a removal has to stay inside the dialog asking it:" +
                Environment.NewLine + string.Join(Environment.NewLine, unreachable));
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// The direction the theory above cannot fail in: it walks the scenes, so a dialog with no scene is not
    /// tested and nothing says so. Same argument as <c>VerifyNoOrphans</c>, pointed the other way.
    /// </summary>
    [Fact]
    public void EveryDialogWindow_IsBuiltByAScene()
    {
        var declared = typeof(Screenshotter).Assembly.GetTypes()
            .Where(type => type.Namespace == "Cockpit.App.Views"
                           && typeof(Window).IsAssignableFrom(type)
                           && type.Name.EndsWith("Dialog", StringComparison.Ordinal))
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        var built = HeadlessAvalonia.Run(() =>
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var scene in Screenshotter.SceneNames)
            {
                var window = Screenshotter.BuildScene(scene);
                names.Add(window.GetType().Name);
            }

            return names;
        });

        var uncovered = declared.Except(built).Order(StringComparer.Ordinal).ToList();
        Assert.True(uncovered.Count == 0,
            "these dialogs have no scene, so nothing above looks at them: " + string.Join(", ", uncovered));
    }

    private static bool _IsDialog(Window window) =>
        window.GetType().Name.EndsWith("Dialog", StringComparison.Ordinal);

    /// <summary>
    /// Gives every piece of text in the footer band a long value. Text inside a scroller is excluded because it
    /// has somewhere to go; text inside a button is excluded because a button asking for the room its own label
    /// needs is what a button is for — the one label here that comes from a caller rather than from the markup
    /// (ConfirmationDialog's) is bounded in the markup instead.
    /// <para>
    /// The band is the bottom <see cref="FooterBand"/> of the window rather than the whole of it, because a
    /// dialog's head holds labels that come from the markup and can be read off it: stressing those reports a
    /// window that no data can produce. The plugin store's search bar is the example — its sort labels sit in a
    /// StackPanel beside two buttons, so a long one would indeed push them out, and no such label exists.
    /// The assertion afterwards still measures every fixed button, head included.
    /// </para>
    /// </summary>
    private static int _StressTheFooterBand(Window window)
    {
        var stressed = 0;
        foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
        {
            if (_Ancestor<ScrollViewer>(text, window) || _Ancestor<Button>(text, window))
            {
                continue;
            }

            var top = (text.TranslatePoint(default, window) ?? default).Y;
            if (top + text.Bounds.Height < window.Bounds.Height - FooterBand)
            {
                continue;
            }

            text.Text = LongEnoughToOverrun;
            stressed++;
        }

        return stressed;
    }

    /// <summary>
    /// The controls an operator cannot reach: laid out past an edge, or squeezed to nothing — which is what
    /// became of Remove in AC-427 when the star column collapsed. One inside a scroller is left out, because it
    /// is reachable by scrolling, and that is the difference this whole guard turns on.
    /// <para>
    /// Not only buttons. A password dialog whose form is cut off has lost the boxes you type into, and a guard
    /// watching the buttons alone reports that as fine — which it did, until a mutation of the scroller that
    /// keeps that form reachable stayed green.
    /// </para>
    /// </summary>
    private List<string> _Unreachable(Window window)
    {
        var unreachable = new List<string>();
        foreach (var control in window.GetVisualDescendants().OfType<Control>().Where(_IsOperated))
        {
            if (!control.IsEffectivelyVisible || _Ancestor<ScrollViewer>(control, window))
            {
                continue;
            }

            var topLeft = control.TranslatePoint(default, window) ?? default;
            var (right, bottom) = (topLeft.X + control.Bounds.Width, topLeft.Y + control.Bounds.Height);
            if (topLeft.X >= -1 && topLeft.Y >= -1
                && right <= window.Bounds.Width + 1 && bottom <= window.Bounds.Height + 1
                && control.Bounds.Width >= 1 && control.Bounds.Height >= 1)
            {
                continue;
            }

            unreachable.Add($"  {_Describe(control)}: x {topLeft.X:0.#}..{right:0.#}, " +
                            $"y {topLeft.Y:0.#}..{bottom:0.#}, size {control.Bounds.Width:0.#}×{control.Bounds.Height:0.#}");
        }

        return unreachable;
    }

    /// <summary>
    /// What an operator works a dialog with. Listed by type rather than taken from <c>Focusable</c>, which is
    /// true of a great deal that is not operated — a ScrollViewer, every item in a list, the window itself —
    /// and would report the contents of a list as unreachable the moment it scrolled.
    /// </summary>
    private static bool _IsOperated(Control control) =>
        control is Button or TextBox or ComboBox or CheckBox or RadioButton or Slider or ToggleSwitch;

    private static string _Describe(Control control) => control switch
    {
        ContentControl { Content: string label } => label,
        TextBox { Watermark: { Length: > 0 } hint } => $"the box for '{hint}'",
        _ => control.Name ?? control.GetType().Name,
    };

    private static bool _Ancestor<T>(Visual control, Window window) where T : Visual
    {
        for (var parent = control.GetVisualParent(); parent is not null && parent != window; parent = parent.GetVisualParent())
        {
            if (parent is T)
            {
                return true;
            }
        }

        return false;
    }
}
