using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Cockpit.Tests.Shared;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-1528: Home in the Simple stand, measured on the scene that renders it (`simple-view-home`).
[Collection("avalonia")]
public sealed class Ac1528SimpleHomeTests
{
    // Criteria 3 and 4 in both composers: image a chip, pdf its path, the mic this box — never the assistant's under Home.
    [Theory]
    [InlineData("home")]
    [InlineData("conversation")]
    public async Task ThePaperclipAndTheMic_LandInTheirOwnBox_NotInTheAssistants(string composer) => await HeadlessAvalonia.RunAsync(async () =>
    {
        var folder = Directory.CreateTempSubdirectory("ac1528-").FullName;
        var window = Screenshotter.ShowScene("simple-view-home");
        try
        {
            var cockpit = (CockpitViewModel)window.DataContext!;
            var transcriber = Substitute.For<IVoicePushToTalkService>();
            transcriber.BeginHold().Returns(true);
            transcriber.EndHoldAsync(Arg.Any<CancellationToken>()).Returns("and the totals");
            var pill = new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), Substitute.For<IVoiceOverlayPresenter>());
            cockpit.SimpleHome.UseDictation(transcriber, pill);

            Func<string> box = () => cockpit.SimpleHome.InputText;
            Func<int> chips = () => cockpit.SimpleHome.PendingAttachments.Count;
            if (composer == "conversation")
            {
                var settings = Substitute.For<IVoiceSettingsStore>();
                settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true });
                var session = TestSessions.Pane(Substitute.For<ISessionManager>(), transcriber, settings, voiceOverlay: pill);
                session.ProjectId = cockpit.Projects.Projects[0].Id;
                cockpit.Sessions.Add(session);
                cockpit.SelectSimpleSessionCommand.Execute(session);
                box = () => session.InputText;
                chips = () => session.PendingAttachments.Count;
            }

            window.UpdateLayout();
            var bar = window.GetVisualDescendants().OfType<SimpleComposerBar>().Single(b => b.IsEffectivelyVisible);
            Assert.Equal(composer == "home", bar.CanChooseProject);
            Assert.Equal(composer == "home" ? "No project or folder" : "Cockpit", bar.ProjectLabel);

            var image = Path.Combine(folder, "chart.png");
            using (var bitmap = new WriteableBitmap(new Avalonia.PixelSize(4, 4), new Avalonia.Vector(96, 96)))
            {
                bitmap.Save(image, PngBitmapEncoderOptions.Default);
            }

            var pdf = Path.Combine(folder, "invoices.pdf");
            await File.WriteAllTextAsync(pdf, "%PDF-1.4");
            await SimpleComposerAttachment.AttachAsync(bar.Target!, image);
            await SimpleComposerAttachment.AttachAsync(bar.Target!, pdf);
            bar.PressMic();
            await bar.ReleaseMicAsync();

            Assert.Equal(1, chips());
            Assert.Equal($"{pdf} and the totals", box());
            Assert.Empty(cockpit.AssistantChat!.InputText);
        }
        finally
        {
            window.Close();
            Directory.Delete(folder, recursive: true);
        }
    });
}
