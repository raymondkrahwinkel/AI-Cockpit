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
