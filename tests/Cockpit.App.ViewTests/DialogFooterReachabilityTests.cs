using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.TestSupport;
using Xunit.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Every dialog keeps the controls an operator works it with inside its own window, whatever its content says
/// (AC-428). AC-427 found one dialog where they did not — the MCP servers window, where a server could be
/// configured and then not saved — and this is the sweep answering "how many others".
/// <para>
/// The mechanism is horizontal, not the height problem it looked like: an element whose width comes from data
/// sharing a track with fixed controls. Neither a <c>Grid</c> nor a <c>StackPanel</c> clips, so the controls are
/// laid out past the window's edge and the window cuts them off. That is what the stress reproduces — every
/// text the window lays out itself is given a long value, because a layout that only holds while a string
/// happens to be short is the defect rather than the absence of one.
/// </para>
/// <para>
/// Written as facts sweeping the scenes rather than a theory per scene, deliberately. Two thirds of the scenes
/// are not dialogs, and a theory case that returns early reads in the runner exactly like one that asserted
/// something. These report what they covered instead.
/// </para>
/// <para>
/// <b>Dialogs only</b>, and the main window was checked rather than assumed: stressing each of the 45 texts it
/// lays out outside a scroller, one at a time, moves nothing operable past an edge, and neither does a session
/// status long enough to fill this comment. Its status bar carries counts, not free text. Only stressing all
/// forty-five at once — which no data can do — produces an offender, so there is nothing here to guard.
/// </para>
/// </summary>
[Collection("avalonia")]
public class DialogFooterReachabilityTests(ITestOutputHelper output)
{
    // Long enough to overrun the widest dialog here (1200) several times over, and a real sentence rather than a
    // run of Xs so it wraps and measures the way the app's own status messages do.
    private const string LongEnoughToOverrun =
        "Hidden here because the cockpit already runs a server by that name: filesystem, fetch, git, ripgrep, " +
        "sequential-thinking, memory and time. Saving removes them — rename yours first if you meant to keep it.";

    /// <summary>
    /// Shorter than the shortest dialog here, so the squeeze bites on all of them rather than only the tall ones —
    /// and shorter than the ticket's 1366×768 laptop panel, which it therefore covers. A dialog declaring a
    /// MinHeight is squeezed to that instead, which is the smallest the clamp will ever make it: the clamp never
    /// goes below a window's own minimum. What the clamp does with a real screen's numbers is arithmetic, and
    /// <c>DialogScreenClampTests</c> holds that against 1280×720 directly.
    /// </summary>
    private const double SqueezedHeight = 200;

    [Fact]
    public void NoDialogLaysAControlOutsideItself_WhateverItsTextSays() => HeadlessAvalonia.Run(() =>
    {
        var (scenes, stressed, bodyless, offenders) = (0, 0, new List<string>(), new List<string>());

        foreach (var scene in Screenshotter.SceneNames)
        {
            var window = _Sized(scene);
            if (window is null)
            {
                continue;
            }

            try
            {
                scenes++;
                var texts = _StressEveryTextThatCameFromData(window);
                stressed += texts;

                // The shared chrome's title bar is built in C#, so it is never in a dialog's own literal set and
                // every scene stresses it. Counted apart from the rest, because a total that always includes it
                // can never reach zero — which would make this line incapable of reporting the gap it exists to
                // report. Measured before this split: 26 of 41 "stressed" texts were title bars.
                if (texts <= 1)
                {
                    bodyless.Add(scene);
                }

                window.UpdateLayout();
                offenders.AddRange(_Unreachable(window).Select(line => $"{scene}: {line}"));
            }
            finally
            {
                window.Close();
            }
        }

        output.WriteLine($"{scenes} dialog scenes, {stressed} texts stressed; nothing beyond the title bar in: " +
                         (bodyless.Count == 0 ? "none" : string.Join(", ", bodyless)));

        Assert.True(offenders.Count == 0,
            "these controls ended up outside the dialog holding them:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    });

    /// <summary>
    /// What the cap is for. A ceiling only helps if the dialog under it gives way in the right place: the part
    /// that scrolls, never the controls. Squeezed to the smallest height it claims to work at — which is what a
    /// small screen does to it — everything operable still has to be inside the window.
    /// <para>
    /// This is the ticket's own mutation run over every dialog rather than one: shrink the height below the
    /// content and see what leaves. It is how the About and plugin-consent dialogs were caught, whose buttons
    /// were the last children of the panel that sized the window.
    /// </para>
    /// </summary>
    [Fact]
    public void NoDialogSqueezedBelowItsContent_LosesAControl() => HeadlessAvalonia.Run(() =>
    {
        var (dialogs, offenders) = (0, new List<string>());

        foreach (var scene in Screenshotter.SceneNames)
        {
            var window = _Sized(scene, squeezed: true);
            if (window is null)
            {
                continue;
            }

            try
            {
                dialogs++;
                offenders.AddRange(_Unreachable(window)
                    .Select(line => $"{scene} at {window.Bounds.Height:0.#} high: {line}"));
            }
            finally
            {
                window.Close();
            }
        }

        output.WriteLine($"{dialogs} dialogs squeezed to {SqueezedHeight:0.#} or their own minimum");

        Assert.True(offenders.Count == 0,
            "these controls left the window when it was squeezed to the smallest height it claims to work at:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    });

    /// <summary>
    /// The other axis. A dialog is centred on its owner with nothing to drag it back by, so one that opens
    /// taller than the screen has put its own buttons past the bottom edge.
    /// <see cref="Cockpit.App.Controls.DialogScreenClamp"/> now runs for every dialog through the shared chrome;
    /// this asserts it reached them, because three of the twenty-one used to ask for it by hand and the other
    /// eighteen simply did not.
    /// <para>
    /// Only the dialogs that measure themselves are asserted on here, because they are the only ones this can
    /// say anything about. A window with a height of its own is <em>resized</em> to fit, and the headless screen
    /// is 1280 high while the tallest such dialog opens at 900 — so "it fits" would be true with the clamp
    /// deleted. That arithmetic is held against a real 1280×720 by <c>DialogScreenClampTests</c> instead, where
    /// it bites.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryDialogThatMeasuresItself_IsCappedAtTheScreenItOpensOn() => HeadlessAvalonia.Run(() =>
    {
        var (capped, unbounded) = (0, new List<string>());

        foreach (var scene in Screenshotter.SceneNames)
        {
            var window = _Sized(scene);
            if (window is null)
            {
                continue;
            }

            try
            {
                if (window.SizeToContent is SizeToContent.Manual)
                {
                    continue;
                }

                capped++;
                var screen = window.Screens.ScreenFromWindow(window);
                Assert.NotNull(screen);

                var available = screen.WorkingArea.Height / screen.Scaling;
                if (window.MaxHeight > available)
                {
                    unbounded.Add($"{scene} may grow to {window.MaxHeight:0.#} on a screen offering {available:0.#}");
                }
            }
            finally
            {
                window.Close();
            }
        }

        output.WriteLine($"{capped} self-measuring dialog scenes capped at the screen");

        Assert.True(capped > 0, "no dialog reached the branch this is about, so it proved nothing");
        Assert.True(unbounded.Count == 0, string.Join(Environment.NewLine, unbounded));
    });

    /// <summary>
    /// The direction the sweeps cannot fail in: they walk the scenes, so a dialog with no scene is not measured
    /// and nothing says so. Same argument as <c>VerifyNoOrphans</c>, pointed the other way.
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
                names.Add(Screenshotter.BuildScene(scene).GetType().Name);
            }

            return names;
        });

        var uncovered = declared.Except(built).Order(StringComparer.Ordinal).ToList();
        Assert.True(uncovered.Count == 0,
            "these dialogs have no scene, so nothing above looks at them: " + string.Join(", ", uncovered));
    }

    /// <summary>
    /// The second band in the profiles dialog's footer, which replaces the first while a removal is confirmed. A
    /// scene shows a dialog in one state, so the sweeps never see this one — a mutation of its columns stayed
    /// green, which is how that was found. Its question carries a profile label the operator typed, so it has the
    /// same shape as the status line it replaces.
    /// </summary>
    [Fact]
    public void TheRemoveConfirmation_KeepsItsAnswersInsideTheDialog() => HeadlessAvalonia.Run(() =>
    {
        // Stressed to the length the status lines are stressed to rather than to a plausible one: the question is
        // what the layout does, not what a reasonable person would name a profile. At 75 characters it fits, and
        // a test that fits proves nothing.
        var window = new Views.ManageProfilesDialog
        {
            DataContext = new ViewModels.ManageProfilesDialogViewModel
            {
                PendingRemovalLabel = LongEnoughToOverrun,
                IsConfirmingRemove = true,
            },
        };

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
    /// The dialog a plugin reaches through <c>ICockpitActions.ConfirmAsync</c>, whose third argument is a free
    /// string. The window is fixed at 440 and cannot be resized, so an unbounded label laid its button out past
    /// the right edge — measured at x=634 for an 86-character one.
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
    /// A scene's window at the size a laptop panel gives it, or null when the scene is not a dialog. Sized before
    /// it is shown, because a headless window keeps the size it opened at — and because
    /// <see cref="Screenshotter.BuildScene"/> applies the size it is handed to the main window only, so asking it
    /// for a laptop-sized dialog quietly does nothing.
    /// </summary>
    private static Window? _Sized(string scene, bool squeezed = false)
    {
        var window = Screenshotter.BuildScene(scene);
        if (!window.GetType().Name.EndsWith("Dialog", StringComparison.Ordinal))
        {
            return null;
        }

        if (squeezed)
        {
            // Set before it is shown: a headless window keeps the size it opened at, so capping it afterwards
            // measures the old layout against a smaller number and reports a failure that is the test's own.
            window.MaxHeight = Math.Max(window.MinHeight, SqueezedHeight);
            window.Height = window.MaxHeight;
        }

        window.Show();
        window.UpdateLayout();

        return window;
    }

    /// <summary>
    /// Gives a long value to every text in the window whose value came from data rather than from the markup —
    /// which is the whole risk, because a string the markup states is a string whose length is already known.
    /// Told apart by reading the dialog's own <c>.axaml</c>: anything rendering a literal that appears there is
    /// left alone. Without that, stressing the command palette's <c>›</c> reports a search box squeezed to 64px
    /// by a prompt character that is one glyph wide and always will be.
    /// <para>
    /// Three exclusions on top. Text inside a scroller has somewhere to go. Text inside a button is a button's
    /// own label — a button asking for the room its label needs is what a button is for; every bound button
    /// label in these dialogs resolves to a short constant in code (<c>"Sign in"</c>, <c>"Install"</c>,
    /// <c>"Save"</c>), and the single label reaching a dialog from outside the program is
    /// <c>ConfirmationDialog</c>'s, which <see cref="AConfirmLabelFromAPlugin_CannotPushItsOwnButtonOffTheDialog"/>
    /// covers by name. Text inside an items control is a row of a list the program itself assembles — the plugin
    /// store's sort modes are the example, and stressing one reported a 1316px combo box in a 1200px window over
    /// a label that reads "Recently added".
    /// </para>
    /// </summary>
    private static int _StressEveryTextThatCameFromData(Window window)
    {
        var literals = _LiteralsIn(window);
        var stressed = 0;
        foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
        {
            // An empty one is stressed rather than skipped, and that is the case this sweep exists for: a status
            // line is empty until something goes wrong, so a scene renders it blank and skipping blanks means
            // never measuring the very element whose length is the defect. Skipping them let a mutation of the
            // profiles dialog's own columns pass — the one this ticket started from.
            if (_Ancestor<ScrollViewer>(text, window) || _Ancestor<Button>(text, window)
                || _Ancestor<ItemsControl>(text, window)
                || (text.Text is { Length: > 0 } rendered
                    && (literals.Contains(rendered) || BoundButNeverLong.Contains(rendered))))
            {
                continue;
            }

            text.Text = LongEnoughToOverrun;
            stressed++;
        }

        return stressed;
    }

    /// <summary>Every <c>Text="…"</c> the dialog's markup states outright, so a rendered one can be recognised.</summary>
    private static HashSet<string> _LiteralsIn(Window window)
    {
        var file = Path.Combine(RepositoryPaths.Root, "src", "Cockpit.App", "Views", $"{window.GetType().Name}.axaml");

        // \b so PlaceholderText= does not come along: it ends in Text and matched, quietly exempting any bound
        // value that happened to read the same as a placeholder ("value", "Repository", "X-Api-Key").
        return File.Exists(file)
            ? [.. Regex.Matches(File.ReadAllText(file), "\\bText=\"([^\"{][^\"]*)\"").Select(match => match.Groups[1].Value)]
            : [];
    }

    /// <summary>
    /// The one bound text this sweep leaves alone: the new-session dialog's login line, which sits in a
    /// StackPanel with the Manage-profiles button and therefore has the shape — but resolves to one of two
    /// constants and can never be longer. Restructuring the band for it moved a button 127px in exchange for
    /// nothing, so the band stays as it is and the exemption is written down instead.
    /// </summary>
    private static readonly HashSet<string> BoundButNeverLong = ["logged in", "not logged in"];

    /// <summary>
    /// Holds the exemption above to its reason. It is worth exactly as much as the claim that those two strings
    /// are all this property can produce, so the property is asked rather than trusted — the day it starts
    /// carrying a provider's name or an error, this fails and the band has to be restructured after all.
    /// </summary>
    [Fact]
    public void TheLoginLineThisSweepExempts_StillCannotBeLong() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ViewModels.NewSessionDialogViewModel();
        var seen = new List<string>();
        foreach (var loggedIn in new[] { true, false })
        {
            viewModel.IsSelectedProfileLoggedIn = loggedIn;
            seen.Add(viewModel.LoginStatusLabel);
        }

        Assert.Equal(BoundButNeverLong.Order(), seen.Order());
    });

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
    private static List<string> _Unreachable(Window window)
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

            unreachable.Add($"{_Describe(control)} at x {topLeft.X:0.#}..{right:0.#}, y {topLeft.Y:0.#}..{bottom:0.#}, " +
                            $"size {control.Bounds.Width:0.#}×{control.Bounds.Height:0.#}, in a window of " +
                            $"{window.Bounds.Width:0.#}×{window.Bounds.Height:0.#}");
        }

        return unreachable;
    }

    /// <summary>
    /// What an operator works a dialog with. Listed by type rather than taken from <c>Focusable</c>, which is
    /// true of a great deal that is not operated — a ScrollViewer, every item in a list, the window itself — and
    /// would report the contents of a list as unreachable the moment it scrolled.
    /// </summary>
    private static bool _IsOperated(Control control) =>
        control is Button or TextBox or ComboBox or CheckBox or RadioButton or Slider or ToggleSwitch;

    private static string _Describe(Control control) => control switch
    {
        ContentControl { Content: string label } => label,
        // A button whose content is a control rather than a string — ConfirmationDialog's trimming label, the
        // icon-plus-text pairs — would otherwise be reported as "Button" and name nothing.
        ContentControl { Content: Control inner } when _FirstText(inner) is { } label => label,
        TextBox { PlaceholderText: { Length: > 0 } hint } => $"the box for '{hint}'",
        _ => control.Name ?? control.GetType().Name,
    };

    private static string? _FirstText(Control control) => control is TextBlock { Text: { Length: > 0 } own }
        ? own
        : control.GetVisualDescendants().OfType<TextBlock>()
            .Select(text => text.Text).FirstOrDefault(text => text is { Length: > 0 });

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
