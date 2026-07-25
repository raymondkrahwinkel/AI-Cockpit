using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Cockpit.App.Plugins;
using Cockpit.App.Views;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="ICockpitHost.CreateMarkdownView"/> (AC-296): the seam a plugin uses to render an issue description or
/// comment through the cockpit's own markdown look instead of showing raw "##"/"**" syntax. Two things are asserted:
/// the interface's own default falls back to the pre-seam plain-text behaviour (so an older host or test fake keeps
/// compiling and rendering unchanged), and the real host renders through <see cref="MarkdownView"/> rather than a
/// second parser.
/// </summary>
public class CockpitHostCreateMarkdownViewTests
{
    [Fact]
    public void DefaultImplementation_ReturnsTheRawTextInAWrappingSelectableTextBlock()
    {
        ICockpitHost host = Substitute.ForPartsOf<HostWithoutMarkdownRendering>();

        var view = host.CreateMarkdownView("## Not rendered\n\nJust the raw text.");

        var text = view.Should().BeOfType<SelectableTextBlock>().Subject;
        text.Text.Should().Be("## Not rendered\n\nJust the raw text.");
        text.TextWrapping.Should().Be(TextWrapping.Wrap);
    }

    [Fact]
    public void HostImplementation_RendersMarkdownInsteadOfLeavingTheRawSyntaxInTheOutput()
    {
        var host = _BuildHost();

        var view = host.CreateMarkdownView("## Heading\n\nBody text.");

        view.Should().BeOfType<MarkdownView>();
        var texts = _CollectText((Control)view).ToList();
        texts.Should().Contain("Heading").And.Contain("Body text.");
        texts.Should().NotContain(text => text.Contains("##"));
    }

    [Fact]
    public void ADescriptionWithinTheBudget_IsRenderedWhole()
    {
        var body = string.Join("\n\n", Enumerable.Range(1, 200).Select(paragraph => $"Paragraph {paragraph}."));

        var view = _BuildHost().CreateMarkdownView(body);

        var texts = _CollectText((Control)view).ToList();
        texts.Should().Contain("Paragraph 200.");
        texts.Should().NotContain(text => text.Contains("truncated"));
    }

    /// <summary>
    /// The body is a third party's — a GitHub issue may hold 65 536 characters, and rendering it builds a control per
    /// cell and per line while the operator waits (AC-303). Cut it, and say so: silently showing two thirds of a
    /// description in a panel whose next button injects that text into an agent is worse than the delay.
    /// </summary>
    [Fact]
    public void ADescriptionPastTheBudget_IsCutAndSaysSo()
    {
        var body = new string('x', 100_000);

        var view = _BuildHost().CreateMarkdownView(body);

        var texts = _CollectText((Control)view).ToList();
        texts.Sum(text => text.Length).Should().BeLessThan(70_000, "the point of the cap is that the oversized part never becomes controls");
        texts.Should().Contain(text => text.Contains("truncated"));
    }

    private static IEnumerable<string> _CollectText(Control? control)
    {
        switch (control)
        {
            case null:
                yield break;
            case SelectableTextBlock textBlock:
                yield return string.Concat((textBlock.Inlines ?? []).OfType<Run>().Select(run => run.Text));
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    foreach (var text in _CollectText(child))
                    {
                        yield return text;
                    }
                }

                break;
            case Border border:
                foreach (var text in _CollectText(border.Child))
                {
                    yield return text;
                }

                break;
            case ContentControl contentControl:
                foreach (var text in _CollectText(contentControl.Content as Control))
                {
                    yield return text;
                }

                break;
        }
    }

    // Typed as the contract a plugin actually holds, so the call goes through ICockpitHost's defaulted parameters —
    // the same way a plugin invokes it.
    private static ICockpitHost _BuildHost() =>
        new CockpitHost(
            "test-plugin",
            "Test Plugin",
            Substitute.For<IServiceProvider>(),
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance);

    /// <summary>An older host: implements only what the contract required before <c>CreateMarkdownView</c> existed.</summary>
    public abstract class HostWithoutMarkdownRendering : ICockpitHost
    {
        public IServiceProvider Services => Substitute.For<IServiceProvider>();

        public ICockpitActions Actions => Substitute.For<ICockpitActions>();

        public IPluginStorage Storage => Substitute.For<IPluginStorage>();

        public void AddSettings(Func<Control> createView)
        {
        }

        public void AddSideMenuButton(string title, Action onInvoke)
        {
        }

        public void AddSideMenuSection(string title, Func<Control> createView)
        {
        }

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
            Task.CompletedTask;
    }
}
