using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-491: the work a project offers, on its card and in its editor. Measured against the real markup for the
/// reason <see cref="ProjectDialogResourceRowTests"/> already gives — the promise here is that someone who cannot
/// write a prompt sees the job and its blast radius, which a view-model-only test cannot tell you.
/// </summary>
[Collection("avalonia")]
public class ProjectJobTests
{
    [Fact]
    public async Task AJobThatDoesNotSayWhatItChanges_IsRefusedWhenTheProjectIsSaved() =>
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var viewModel = new ProjectDialogViewModel();
            var window = new ProjectDialog { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            var addJob = window.GetVisualDescendants().OfType<Button>()
                .First(button => ReferenceEquals(button.Command, viewModel.AddJobCommand));
            addJob.Command!.Execute(null);
            window.UpdateLayout();
            viewModel.Jobs.Single().Prompt = "Process this month's invoices";

            var closed = false;
            viewModel.CloseRequested += _ => closed = true;
            await viewModel.SaveCommand.ExecuteAsync(null);
            window.Close();

            // Refused, not quietly saved without the line: a button whose blast radius the operator has to guess is
            // exactly what this feature exists to prevent.
            Assert.False(closed);
            Assert.NotNull(viewModel.SaveError);
        });

    [Fact]
    public void AProjectWithJobs_ShowsEachWithItsLine_AndStillOffersThePlainStart() => HeadlessAvalonia.Run(() =>
    {
        var project = Project.Create("Invoices") with
        {
            DefaultProfileLabel = "personal",
            Jobs = [new ProjectJob("Process this month's invoices", "changes nothing · reports only")],
        };
        var window = new Window
        {
            Width = 278,
            Height = 420,
            Content = new ProjectCardView { DataContext = new ProjectCardViewModel(project, "● This machine") },
        };
        window.Show();
        window.UpdateLayout();

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text)
            .ToList();
        window.Close();

        Assert.Contains("Process this month's invoices", texts);
        Assert.Contains("changes nothing · reports only", texts);
        // The free route stays beside the list rather than being replaced by it: Start is what opens a session with
        // an empty box for whoever would rather type their own.
        Assert.Contains("Start", texts);
    });
}
