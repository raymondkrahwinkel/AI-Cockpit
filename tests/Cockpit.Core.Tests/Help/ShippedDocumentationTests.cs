using Cockpit.App.Services;
using Cockpit.Core.Help;

namespace Cockpit.Core.Tests.Help;

// AC-1040: the app's own pages, read out of the built assembly the way the window reads them.
public sealed class ShippedDocumentationTests
{
    // A category with no page in it is hidden, so a branch the navigation promises can go missing without
    // anything failing — which is the state `System` was in before this ticket. Asserted per category rather
    // than as a count: the interesting failure is one branch emptying, not the total moving.
    [Theory]
    [InlineData(HelpCategory.General)]
    [InlineData(HelpCategory.System)]
    [InlineData(HelpCategory.ExtendingCockpit)]
    public void TheAppFillsEveryBranchOfItsOwnNavigation(HelpCategory category) =>
        Assert.Contains(
            HelpDocumentScanner.Scan(typeof(HelpService).Assembly, HelpOwner.Core),
            article => article.Category == category);
}
