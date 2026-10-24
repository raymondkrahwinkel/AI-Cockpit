using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;

namespace Cockpit.Journeys;

// AC-1532: Earlier conversations in the Simple stand — the sheet opened from Home, searched, filtered, and Continue.
[Collection(JourneyCollection.Alone)]
public sealed class EarlierConversationsJourney
{
    // Criterion 1: the sheet lies over Home with the search box focused; Esc closes it and Home is as it was.
    // Criteria 2 and 3: "jansen" finds it in a title and in what was said, with the word marked and the count right;
    // Sales plus This week then leaves only the conversation of that project from that week.
    [Fact]
    public async Task FromHome_TheSheetSearchesByTitleAndWhatWasSaid_TheFiltersNarrowToAProjectAndAWeek_AndEscLeavesHomeAsItWas()
    {
        await using var cockpit = JourneyHost.Desktop();
        var projects = cockpit.Services.GetRequiredService<IProjectEditor>();
        var sales = await projects.AddNewProjectAsync(Project.Create("Sales"));
        var finance = await projects.AddNewProjectAsync(Project.Create("Finance"));
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.FromHours(2));
        var index = cockpit.Services.GetRequiredService<IConversationIndex>();
        async Task SeedAsync(string title, Project? project, TimeSpan ago, string said)
        {
            var transcript = Path.Combine(cockpit.StateRoot, $"{Guid.NewGuid():N}.jsonl");
            await File.WriteAllTextAsync(transcript, $$"""{"Kind":"UserText","Text":"{{said}}"}""" + Environment.NewLine);
            await index.RecordAsync(new ConversationRecord(Guid.NewGuid().ToString("N"), title, project?.Id, cockpit.StateRoot, JourneyHost.EchoProfile, "claude",
                "c", SessionConversationIdState.Known, now - ago, now - ago, "Done", now - ago, false, false, transcript));
        }

        await SeedAsync("Quote for Jansen", sales, TimeSpan.FromDays(1), "send it on Friday");
        await SeedAsync("Q3 report", finance, TimeSpan.FromHours(2), "three large invoices to Jansen in September");
        await SeedAsync("Update customer details", sales, TimeSpan.FromDays(20), "the address of Jansen B.V. changed");
        await SeedAsync("Website copy", null, TimeSpan.FromHours(1), "three pages ready to publish");
        await cockpit.StartDesktopAsync();

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (app, window) = _OnHome(cockpit);
            var sheet = app.EarlierConversations;
            sheet.Now = () => now;
            EarlierConversationsSheet? shown = null;
            await Step.Run("the sheet opens over Home with the search focused", async () =>
            {
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "HomeEarlierConversations");
                await Assert.IsAssignableFrom<IAsyncRelayCommand>(button.Command).ExecuteAsync(null);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                shown = window.GetVisualDescendants().OfType<EarlierConversationsSheet>().Single();
                Assert.True(shown.IsEffectivelyVisible);
                Assert.Equal("EarlierSearch", (window.FocusManager?.GetFocusedElement() as Control)?.Name);
            });

            await Step.Run("searching finds by title and what was said, and the filters narrow", async () =>
            {
                window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "EarlierSearch").Text = "jansen";
                await Until.Holds(sheet, () => sheet.FoundLabel == "3 conversations found");
                Assert.Equal(["Q3 report", "Quote for Jansen", "Update customer details"], sheet.Rows.Select(row => row.Record.Title));
                Assert.Equal(["TODAY", "THIS WEEK", "EARLIER"], sheet.Rows.Select(row => row.GroupHeader));
                Assert.Equal(("Quote for ", "Jansen"), (sheet.Rows[1].Title.Before, sheet.Rows[1].Title.Match));
                var fragment = sheet.Rows[0].Fragment;
                Assert.NotNull(fragment);
                Assert.Equal(("three large invoices to ", "Jansen", " in September"), (fragment.Before, fragment.Match, fragment.After));

                window.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "EarlierProjectFilter").SelectedItem =
                    sheet.ProjectFilters.Single(filter => filter.Label == "Sales");
                window.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "EarlierDateFilter").SelectedItem =
                    sheet.DateFilters.Single(filter => filter.Label == "This week");
                await Until.Holds(sheet, () => sheet.FoundLabel == "1 conversation found");
                Assert.Equal(["Quote for Jansen"], sheet.Rows.Select(row => row.Record.Title));
            });

            await Step.Run("Esc leaves Home as it was", () =>
            {
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                window.UpdateLayout();
                Assert.False(shown?.IsEffectivelyVisible);
                Assert.Equal(SimplePage.Home, app.SimplePage);
                Assert.True(window.GetVisualDescendants().OfType<SimpleHomeView>().Single().IsEffectivelyVisible);
                return Task.CompletedTask;
            });
        });
    }

    // Criteria 4 and 5: Continue on a closed conversation closes the sheet and brings it back with what was said, and
    // the next message goes on in it; Continue on it while open selects it, and the assistant's row shows the assistant.
    [Fact]
    public async Task Continue_BringsAClosedConversationBackToGoOnIn_SelectsAnOpenOne_AndShowsTheAssistant()
    {
        await using var cockpit = JourneyHost.Desktop();
        var project = await cockpit.SaveProfileAndProjectAsync(Directory.CreateDirectory(Path.Combine(cockpit.StateRoot, "project")).FullName);
        await cockpit.Services.GetRequiredService<IAssistantSettingsStore>().SaveAsync(new AssistantSettings { IsEnabled = true });
        await cockpit.Services.GetRequiredService<IAssistantProfileStore>().RepointAsync(
            new SessionProfile("Assistant", new ClaudeConfig(Path.Combine(cockpit.StateRoot, ".claude"))) { DefaultKind = ProfileSessionKind.Sdk },
            replacesStandingInstruction: false);
        await cockpit.StartDesktopAsync();
        var closed = await cockpit.StartSessionThroughTheDialogAsync(project);

        await HeadlessAvalonia.RunAsync(async () =>
        {
            closed.InputText = "history";
            await closed.SendCommand.ExecuteAsync(null);
            await Until.CollectionHolds(closed.Transcript, () => closed.Transcript.Any(row => row.Text == "echo: history"));
            await cockpit.Cockpit.CloseSessionCommand.ExecuteAsync(closed);

            var (app, window) = _OnHome(cockpit);
            var chat = app.AssistantChat ?? throw new InvalidOperationException("The assistant has no conversation.");
            chat.InputText = "what is open?";
            await chat.SendCommand.ExecuteAsync(null);
            var assistant = chat.Session ?? throw new InvalidOperationException("The assistant did not start.");
            await Until.CollectionHolds(assistant.Transcript, () => assistant.Transcript.Any(row => row.Text == "echo: what is open?"));

            var sheet = app.EarlierConversations;
            async Task<EarlierConversationRow> OpenOnAsync(Func<EarlierConversationRow, bool> which)
            {
                app.ShowSimpleStartScreenCommand.Execute(null);
                await sheet.OpenCommand.ExecuteAsync(null);
                await Until.CollectionHolds(sheet.Rows, () => sheet.Rows.Any(which));
                sheet.Selected = sheet.Rows.First(which);
                window.UpdateLayout();
                return sheet.Selected;
            }

            var row = await OpenOnAsync(candidate => candidate.Record.PaneId == closed.PaneId);
            var shown = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("continue") && b.IsEffectivelyVisible);
            Assert.Same(row, shown.DataContext);
            await ((IAsyncRelayCommand)shown.Command!).ExecuteAsync(shown.CommandParameter);
            Assert.False(sheet.IsOpen);
            var reopened = Assert.IsType<SessionViewModel>(app.SimpleSelectedSession);
            Assert.Equal((closed.PaneId, SimplePage.Conversation), (reopened.PaneId, app.SimplePage));
            Assert.Equal(SessionResume.BySessionId("echo-conversation"), cockpit.Drivers.Made[^1].Resume);
            await Until.CollectionHolds(reopened.Transcript, () => reopened.Transcript.Any(r => r.Text == "echo: history"));
            reopened.InputText = "and now?";
            await reopened.SendCommand.ExecuteAsync(null);
            await Until.CollectionHolds(reopened.Transcript, () => reopened.Transcript.Any(r => r.Text == "echo: and now?"));

            var made = cockpit.Drivers.Made.Count;
            await OpenOnAsync(candidate => candidate.Record.PaneId == closed.PaneId);
            Dispatcher.UIThread.RunJobs();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Until.Holds(sheet, () => !sheet.IsOpen);
            Assert.Equal((reopened, made), (app.SimpleSelectedSession, cockpit.Drivers.Made.Count));

            var own = await OpenOnAsync(candidate => candidate.Record.PaneId == AssistantIdentity.PaneId);
            Assert.Equal(("Assistant", null, true), (own.Profile, own.ProviderBadge, own.CanContinue));
            await sheet.ContinueCommand.ExecuteAsync(own);
            Assert.Equal((assistant, false), (app.SimpleSelectedSession, sheet.IsOpen));
        });
    }

    private static (CockpitViewModel App, Window Window) _OnHome(JourneyHost cockpit)
    {
        var app = cockpit.Cockpit;
        var window = cockpit.Window;
        window.Width = 1280;
        window.Height = 800;
        app.ShowSimpleViewCommand.Execute(null);
        app.ShowSimpleStartScreenCommand.Execute(null);
        window.UpdateLayout();
        return (app, window);
    }
}
