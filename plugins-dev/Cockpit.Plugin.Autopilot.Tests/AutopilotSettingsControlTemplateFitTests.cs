using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Cockpit.Plugins.Abstractions;
using NSubstitute;
using Xunit.Abstractions;

namespace Cockpit.Plugin.Autopilot.Tests;

// AC-521 Iron Law #9: unlike the YouTrack/GitHub-Issues/GitHub-Pull-Requests plugins (whose placeholder help lives
// in a "?" tooltip and so costs no visible layout), this plugin's Templates section has no tooltip idiom at all —
// the placeholder help is a plain, always-visible `_Hint`. Enriching its text really does grow the visible
// page, so this measures it rather than assuming a tooltip's usual "invisible until hovered" safety applies here.
//
// The enriched hint is the *last* child added to the Templates section — after the header, the intro hint,
// the "+ New template" button and the template list — so growing it cannot push any of those earlier, actionable
// controls down or off screen; only more of the (already scrollable, informational) tail grows. This pins that
// claim with a measurement instead of an assertion in prose.
[Collection("avalonia")]
public class AutopilotSettingsControlTemplateFitTests
{
    private readonly ITestOutputHelper _out;

    public AutopilotSettingsControlTemplateFitTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void NewTemplateButton_StaysAtItsOriginalPosition_UnaffectedByTheLongerHintBelowIt()
    {
        var control = _Control();
        control.ShowSection(3); // "Templates" — see AutopilotSettingsSectionsTests.

        // The window a real plugin-settings dialog opens at (CockpitViewModel.OpenPluginSettingsAsync: 640x560).
        const double width = 640;
        const double height = 560;

        var window = new Window { Width = width, Height = height, Content = control };
        window.Show();
        window.UpdateLayout();

        var target = new RenderTargetBitmap(new PixelSize((int)width, (int)height));
        target.Render(window);

        var newTemplateButton = control.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "+ New template"));
        var help = control.GetVisualDescendants().OfType<TextBlock>().Single(block => (block.Text ?? string.Empty).StartsWith("Placeholders you can use in a body"));

        var buttonTop = newTemplateButton.TranslatePoint(default, window) ?? default;
        var helpTop = help.TranslatePoint(default, window) ?? default;
        var helpBottom = help.TranslatePoint(new Point(0, help.Bounds.Height), window) ?? default;

        _out.WriteLine($"window={width}x{height}  newTemplateButton.Top={buttonTop.Y:0.#}  " +
                       $"help.Top={helpTop.Y:0.#}  help.Height={help.Bounds.Height:0.#}  help.Bottom={helpBottom.Y:0.#}");

        window.Close();

        // "+ New template" is the second control in the section (after the header/intro hint) and comes BEFORE the
        // enriched help — nothing above it changed, so it must still sit near the top regardless of how long the
        // help text below it grew. 80px is generous headroom for a header + one intro hint line at this font size.
        Assert.True(buttonTop.Y < 80, $"the '+ New template' button moved down to {buttonTop.Y:0.#}px — something before it in the section grew, not just the trailing help");
        Assert.True(help.Bounds.Height > 0, "the placeholder help collapsed to zero height");
    }

    [Fact]
    public void TemplateRow_ALongNameDoesNotOverlapTheEditAndResetButtons()
    {
        var storage = new FakeStorage();
        var host = Substitute.For<ICockpitHost>();
        host.RegisteredAutopilotTemplates.Returns([]);
        host.CreateHelpHint(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(_ => new Panel());
        var templates = new AutopilotTemplateStore(storage);
        templates.UpsertUserTemplate(AutopilotTemplate.ForUser(
            "user.long", "A template name long enough to run the whole width of the row and then quite a lot further past it, well beyond where the buttons sit", "body"));

        var control = new AutopilotSettingsControl(new AutopilotSettings(storage), host, templates);
        control.ShowSection(3); // "Templates"

        const double width = 640;
        const double height = 560;
        var window = new Window { Width = width, Height = height, Content = control };
        window.Show();
        window.UpdateLayout();

        var edit = control.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Edit"));
        var name = control.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => block.Text != null && block.Text.StartsWith("A template name long enough"));

        var nameRight = (name.TranslatePoint(new Point(name.Bounds.Width, 0), window) ?? default).X;
        var editLeft = (edit.TranslatePoint(default, window) ?? default).X;

        _out.WriteLine($"name right={nameRight:0.#} edit left={editLeft:0.#} in a {width}-wide window");

        window.Close();

        Assert.True(nameRight <= editLeft + 1,
            $"the long template name (ending at {nameRight:0.#}) must not overlap the Edit button (starting at {editLeft:0.#})");
    }

    private static AutopilotSettingsControl _Control()
    {
        var storage = new FakeStorage();
        var host = Substitute.For<ICockpitHost>();
        host.RegisteredAutopilotTemplates.Returns([]);
        host.CreateHelpHint(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(_ => new Panel());

        return new AutopilotSettingsControl(new AutopilotSettings(storage), host, new AutopilotTemplateStore(storage));
    }

    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var value) && value is T typed ? typed : default;

        public void Set<T>(string key, T value) => _data[key] = value;
    }
}
