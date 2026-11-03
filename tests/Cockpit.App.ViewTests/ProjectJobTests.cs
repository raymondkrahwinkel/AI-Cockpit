using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewTests;

// AC-491: measured on the real markup — the promise is that someone who cannot write a prompt sees a job and its blast radius.
[Collection("avalonia")]
public class ProjectJobTests
{
    // AC-493 criterion 3: read from the rule, not the day the cockpit opened — that failure looks entirely ordinary on screen.
    [Fact]
    public void ARecurringJob_ShowsItsRuleAndWhenItLastCameRound() => HeadlessAvalonia.Run(() =>
    {
        var project = Project.Create("Invoices") with
        {
            DefaultProfileLabel = "personal",
            Jobs =
            [
                new ProjectJob(
                    "Process this month's invoices",
                    "changes nothing · reports only",
                    new JobRecurrence(2, DayOfWeek.Monday)),
            ],
        };
        var window = new Window
        {
            Width = 278,
            Height = 420,
            // Standing on 3 September, a cockpit shut since August: a reminder that can only be checked by
            // waiting for a real second Monday is a reminder nobody checks.
            Content = new ProjectCardView
            {
                DataContext = new ProjectCardViewModel(project, "● This machine", today: new DateOnly(2026, 9, 3)),
            },
        };
        window.Show();
        window.UpdateLayout();

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text)
            .ToList();
        window.Close();

        // August's second Monday — not September's, which is still ahead, and not the day it opened. The rule
        // stands beside the date because the date alone would read as "you did not do this", which is the one
        // thing a rule cannot know.
        Assert.Contains("second Monday of the month · last on 10 August", texts);
    });
}
