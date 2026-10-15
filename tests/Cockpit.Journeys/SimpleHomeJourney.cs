using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Consent;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Journeys;

// AC-1528: Home in the Simple stand, on the desktop as the operator meets it — typed into, sent, and found again.
[Collection(JourneyCollection.Alone)]
public sealed class SimpleHomeJourney
{
    private const string ProjectProfile = "ProjectProfile";

    // Criteria 1, 2, 2b and 8: Send starts its own conversation where the chip says, in view, and the rail finds it again.
    [Theory]
    [InlineData("the profile's folder")]
    [InlineData("Documents\\Cockpit")]
    [InlineData("a project")]
    [InlineData("a chosen folder")]
    public async Task SendOnHome_StartsAConversationOfItsOwn_WhereTheChipSays(string place)
    {
        await using var cockpit = JourneyHost.Desktop();
        var profileFolder = place == "the profile's folder" ? Path.Combine(cockpit.StateRoot, "profile") : null;
        var project = await _ProfilesAndProjectAsync(cockpit, profileFolder);
        await cockpit.StartDesktopAsync();

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (app, window) = _OnHome(cockpit);
            var chosen = Directory.CreateDirectory(Path.Combine(cockpit.StateRoot, "chosen")).FullName;
            if (place == "a project")
            {
                app.SimpleHome.ChooseProjectCommand.Execute(project);
            }
            else if (place == "a chosen folder")
            {
                app.SimpleHome.ChooseFolder(chosen);
            }

            var assistantSaid = app.AssistantChat?.Session?.Transcript.Count;
            var session = await _SendAsync(app, window, "what is still open?");

            var driver = cockpit.Drivers.Made[^1];
            var (folder, profile) = place switch
            {
                "the profile's folder" => (profileFolder, JourneyHost.EchoProfile),
                "Documents\\Cockpit" => (app.SimpleFallbackFolder, JourneyHost.EchoProfile),
                "a project" => (project.SourceDirectory, ProjectProfile),
                _ => (chosen, JourneyHost.EchoProfile),
            };
            Assert.Equal(folder, driver.WorkingDirectory);
            Assert.True(Directory.Exists(folder));
            Assert.Equal(profile, driver.Profile?.Label);
            Assert.Contains(window.GetVisualDescendants().OfType<SessionView>(), view => view.IsEffectivelyVisible && view.DataContext == session);
            Assert.Equal(assistantSaid, app.AssistantChat?.Session?.Transcript.Count);

            app.ShowSimpleStartScreenCommand.Execute(null);
            window.UpdateLayout();
            var row = window.GetVisualDescendants().OfType<Button>()
                .Single(button => button.IsEffectivelyVisible && button.DataContext == session && button.Command == app.SelectSimpleSessionCommand);
            row.Command!.Execute(row.CommandParameter);
            Assert.Same(session, app.SimpleSelectedSession);
        });
    }

    // Criterion 3, sent: an image picked with the paperclip shows as a chip and goes along as an image, a pdf as its path.
    [Fact]
    public async Task OnHome_AnImageGoesAlongAsAnImage_AndAPdfAsItsPath()
    {
        await using var cockpit = JourneyHost.Desktop();
        await _ProfilesAndProjectAsync(cockpit, profileFolder: null);
        await cockpit.StartDesktopAsync();

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (app, window) = _OnHome(cockpit);
            var image = Path.Combine(cockpit.StateRoot, "chart.png");
            using (var bitmap = new WriteableBitmap(new Avalonia.PixelSize(4, 4), new Avalonia.Vector(96, 96)))
            {
                bitmap.Save(image, PngBitmapEncoderOptions.Default);
            }

            var pdf = Path.Combine(cockpit.StateRoot, "invoices.pdf");
            await File.WriteAllTextAsync(pdf, "%PDF-1.4");
            var session = await _SendAsync(app, window, "add these up", async () =>
            {
                await SimpleComposerAttachment.AttachAsync(app.SimpleHome, image);
                await SimpleComposerAttachment.AttachAsync(app.SimpleHome, pdf);
                window.UpdateLayout();
                Assert.Single(window.GetVisualDescendants().OfType<Image>(), shown => shown.IsEffectivelyVisible && shown.DataContext is ImageAttachmentViewModel);
            });

            Assert.Single(cockpit.Drivers.Made[^1].ImagesAtSend);
            Assert.Contains(session.Transcript, row => row.Kind == TranscriptEntryKind.UserText && row.Text == $"add these up {pdf}");
            Assert.Empty(app.SimpleHome.PendingAttachments);
        });
    }

    // Criterion 5: a consent waiting is a row whose Review shows it; answered, the row and then the block go.
    [Fact]
    public async Task WaitingForYou_ReviewShowsTheConversation_AndTheAnswerTakesTheRowAway()
    {
        await using var cockpit = JourneyHost.Desktop();
        await _ProfilesAndProjectAsync(cockpit, profileFolder: null);
        await cockpit.StartDesktopAsync();

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (app, window) = _OnHome(cockpit);
            var session = await _SendAsync(app, window, "publish the pages");
            app.ShowSimpleStartScreenCommand.Execute(null);

            var broker = cockpit.Services.GetRequiredService<IConsentBroker>();
            _ = broker.RequestConsentAsync(new ConsentRequest(
                "Publish three pages", "publish website/pages", new ConsentSource(session.PaneId, null, "Website"), "site", ConsentRisk.LowRisk));
            await Until.Holds(session, () => session.PendingConsent is not null);
            window.UpdateLayout();

            var block = window.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "WaitingForYou");
            Assert.True(block.IsEffectivelyVisible);
            var review = block.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext == session);
            review.Command!.Execute(review.CommandParameter);
            Assert.Same(session, app.SimpleSelectedSession);
            Assert.False(app.SimpleStandShowsTheStartScreen);

            app.ShowSimpleStartScreenCommand.Execute(null);
            broker.Respond(session.PendingConsent!.Id, ConsentOutcome.Approved, remember: false);
            await Until.Holds(app, () => !app.HasSimpleWaitingForYou);
            window.UpdateLayout();
            Assert.False(block.IsEffectivelyVisible);
        });
    }

    // The default profile (the first) and a project on a profile of its own, so where a conversation runs is visible in both.
    private static async Task<Project> _ProfilesAndProjectAsync(JourneyHost cockpit, string? profileFolder)
    {
        var claude = new ClaudeConfig(Path.Combine(cockpit.StateRoot, ".claude"));
        await cockpit.Services.GetRequiredService<ISessionProfileStore>().SaveAsync(
        [
            new SessionProfile(JourneyHost.EchoProfile, claude) { DefaultKind = ProfileSessionKind.Sdk, DefaultWorkingDirectory = profileFolder },
            new SessionProfile(ProjectProfile, claude) { DefaultKind = ProfileSessionKind.Sdk },
        ]);
        var folder = Directory.CreateDirectory(Path.Combine(cockpit.StateRoot, "project")).FullName;
        if (profileFolder is not null)
        {
            Directory.CreateDirectory(profileFolder);
        }

        return await cockpit.Services.GetRequiredService<IProjectEditor>().AddNewProjectAsync(
            Project.Create("Journey") with { SourceDirectories = [new ProjectRepository(folder)], DefaultProfileLabel = ProjectProfile });
    }

    private static (CockpitViewModel App, Window Window) _OnHome(JourneyHost cockpit)
    {
        var app = cockpit.Cockpit;
        app.SimpleFallbackFolder = Path.Combine(cockpit.StateRoot, "Documents", "Cockpit");
        var window = cockpit.Window;
        window.Width = 1280;
        window.Height = 800;
        app.ShowSimpleViewCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(window.GetVisualDescendants().OfType<SimpleHomeView>().Single().IsEffectivelyVisible);
        return (app, window);
    }

    // Typed into Home's own box and sent with its own button; returns the conversation once it has answered.
    private static async Task<SessionViewModel> _SendAsync(CockpitViewModel app, Window window, string text, Func<Task>? afterTyping = null)
    {
        var input = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "HomeInput");
        input.Text = text;
        await (afterTyping?.Invoke() ?? Task.CompletedTask);
        var sent = input.Text;
        var send = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "HomeSendButton");
        await ((IAsyncRelayCommand)send.Command!).ExecuteAsync(null);

        var session = Assert.IsType<SessionViewModel>(app.SimpleSelectedSession);
        await Until.CollectionHolds(session.Transcript, () => session.Transcript.Any(row => row.Kind == TranscriptEntryKind.AssistantText && row.Text == $"echo: {sent}"));
        Assert.Empty(input.Text ?? string.Empty);
        return session;
    }
}
