extern alias UiAsm;

using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Notes;
using Cockpit.Plugins.Abstractions;
using NSubstitute;
using DepotMeetingNoteScene = UiAsm::Cockpit.Plugin.Depot.UI.DepotMeetingNoteScene;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1579: the "New meeting note" dialog, from the backend call to the pixels. Layer 2.
[Collection("avalonia")]
public class DepotMeetingNoteTests
{
    // Acceptance 1: mockup 2a (the dialog), in Dark and Light. With DEPOT_MEETING_NOTE_SCENE_DIR set to a folder, the frames
    // are written there as <scene>-<theme>.png (AC-1235 style).
    [Fact]
    public void Dialog_RendersMockup2a_InBothThemes()
    {
        Assert.All(_RenderInBothThemes("2a", created: false), rendered =>
        {
            Assert.True(rendered.Width > 0);
            Assert.All(new[] { "Depot project", "Topic", "File", "Template", "Attendees", "2026-10-19 Sprint review.md" },
                label => Assert.Contains(label, rendered.Texts));
            Assert.True(rendered.Dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked);
            Assert.True(rendered.Dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Create and open").IsEnabled);
        });
    }

    // Acceptance 1: mockup 2b (the created state), in Dark and Light.
    [Fact]
    public void Dialog_RendersMockup2b_InBothThemes()
    {
        Assert.All(_RenderInBothThemes("2b", created: true), rendered =>
        {
            Assert.True(rendered.Width > 0);
            Assert.Contains(rendered.Texts, text => text?.StartsWith("✓ Created in depot › Meetings.", StringComparison.Ordinal) == true && text.Contains("Sanne and Jithran got an invitation", StringComparison.Ordinal));
            Assert.Contains(rendered.Dialog.GetVisualDescendants().OfType<Button>(), button => button.Content as string == "Open in Cockpit");
        });
    }

    private static List<(int Width, List<string?> Texts, Control Dialog)> _RenderInBothThemes(string scene, bool created)
    {
        Avalonia.Threading.AvaloniaSynchronizationContext.InstallIfNeeded();
        var application = Application.Current ?? throw new InvalidOperationException("No application.");
        var rendered = new List<(int Width, List<string?> Texts, Control Dialog)>();
        try
        {
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                application.RequestedThemeVariant = theme;
                var dialog = DepotMeetingNoteScene.Dialog(created);
                var window = new Window { Width = 560, Height = created ? 200 : 700, Content = dialog };
                window.Show();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
                if (Environment.GetEnvironmentVariable("DEPOT_MEETING_NOTE_SCENE_DIR") is { Length: > 0 } directory)
                {
                    using var stream = File.Create(Path.Combine(directory, $"{scene}-{$"{theme.Key}".ToLowerInvariant()}.png"));
                    frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }

                rendered.Add((frame.PixelSize.Width, dialog.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList(), dialog));
                window.Close();
            }
        }
        finally
        {
            application.RequestedThemeVariant = ThemeVariant.Dark;
        }

        return rendered;
    }

    // Acceptances 2 and 3: the list is what `options` answers (a project you only view is left out by Depot and counted),
    // and Create sends the choices only. Counter-proof: add a "content" field to the form in DepotMeetingNoteService.CreateAsync
    // and the form assertion fails.
    [Fact]
    public async Task Create_SendsOnlyTheChoices_AndTheListIsWhatOptionsAnswers()
    {
        var calls = new List<(HttpMethod Method, string Path, string Body)>();
        var host = Substitute.For<ICockpitHost>();
        host.CreateAuthorizedHandler(Arg.Any<string>()).Returns(_ => new _Depot(calls));
        var service = new DepotMeetingNoteService(host, () => [new DepotConnectionRegistration("c1", "Work", "https://depot.example.com")], () => new DateOnly(2026, 10, 19));

        var options = await service.OptionsAsync(new("c1"), CancellationToken.None);
        var created = await service.CreateAsync(
            new("c1", "acme/depot", "Sprint review", "Meetings", DepotMeetingNoteTemplate.Meeting, ["u1", "u2"]), CancellationToken.None);

        var listed = Assert.IsType<DepotMeetingNoteOptions>(options.Data);
        Assert.Equal(["acme/depot"], listed.Projects.Select(project => project.Address));
        Assert.Equal(1, listed.LeftOut);
        Assert.Equal("Meetings/2026-10-19 Sprint review.md", Assert.IsType<DepotMeetingNoteCreated>(created.Data).Path);
        var post = Assert.Single(calls, call => call.Method == HttpMethod.Post);
        Assert.Equal("/cockpit/projects/acme/depot/meeting-note", post.Path);
        Assert.Equal(
            ["path=Meetings/2026-10-19 Sprint review.md", "topic=Sprint review", "template=meeting", "date=2026-10-19", "attendee=u1", "attendee=u2"],
            post.Body.Split('&').Select(field => Uri.UnescapeDataString(field.Replace('+', ' '))));
        Assert.DoesNotContain(typeof(DepotMeetingNoteRequest).GetProperties(), property => property.Name.Contains("Content", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class _Depot(List<(HttpMethod Method, string Path, string Body)> calls) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            calls.Add((request.Method, request.RequestUri?.AbsolutePath ?? string.Empty, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            var json = request.Method == HttpMethod.Post
                ? """{"ok":true,"message":"Meetings/2026-10-19 Sprint review.md"}"""
                : """{"projects":[{"address":"acme/depot","name":"depot","organization":"Acme","role":"Editor","folder":"Meetings","allowed":true}],"leftOut":1,"recent":[]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
