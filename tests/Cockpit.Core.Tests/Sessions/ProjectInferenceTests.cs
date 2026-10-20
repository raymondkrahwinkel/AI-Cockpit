using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Tests.Shared;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

// Assistant threads: a prompt typed in All or spoken is filed by what it names before its turn runs — a session, a
// ticket key, a project's name — so filing does not hang on the assistant remembering to call set_thread.
public class ProjectInferenceTests
{
    // Names and prefixes as an operator's projects carry them, including a shared word and a prefix with its dash.
    private static readonly ProjectHint[] Projects =
    [
        new("depot", "Depot", ["DEP"]),
        new("cockpit", "Cockpit", ["AC"]),
        new("vov", "Keuzehub Voorlichting", ["VOV"]),
        new("flow", "Synvolution Flow", ["SF"]),
        new("site", "Synvolution Website", []),
        new("pp", "Payroll Processor", ["PP"]),
    ];

    private static readonly SessionHint[] Sessions = [new("pane-9", "AC-1594 parallel depot reads", "cockpit")];

    private static IReadOnlyList<(string ProjectId, ProjectSource Source)> Infer(string text) =>
        ProjectInference.Infer(text, Projects, Sessions);

    [Theory]
    [InlineData("hoe staat Depot ervoor?", "depot")]
    [InlineData("staat er nog wat open voor keuzehub voorlichytingen?", "vov")]
    [InlineData("staat er nog iets open op VOV", "vov")]
    [InlineData("kijk even naar dep 422", "depot")]
    [InlineData("wat doet PP-12 nu", "pp")]
    public void APromptNamingOneProject_IsFiledUnderIt(string prompt, string project) =>
        Assert.Equal(project, Assert.Single(Infer(prompt)).ProjectId);

    [Theory]
    [InlineData("de website van synvolution moet om")]
    [InlineData("zet donderdag 14:00 tandarts in mijn agenda")]
    [InlineData("dat is een vov-gevoel")]
    public void APromptNamingNoProject_StaysGeneral(string prompt) => Assert.Empty(Infer(prompt));

    [Fact]
    public void APromptAboutTwoProjects_IsInBoth_InTheOrderItNamesThem()
    {
        var hits = Infer("voor WayMark niets, maar voor DEP-422 de route en voor de cockpit de test");

        Assert.Equal(["depot", "cockpit"], hits.Select(hit => hit.ProjectId));
        Assert.Equal(ProjectSource.TicketPrefix, hits[0].Source);
        Assert.Equal(ProjectSource.Mentioned, hits[1].Source);
    }

    [Fact]
    public void ATicketKeyALiveSessionCarries_HasTheSessionAsItsSource()
    {
        Assert.Equal(("cockpit", ProjectSource.Session), Assert.Single(Infer("hoe ver is AC-1594?")));
        Assert.Equal(("cockpit", ProjectSource.Session), Assert.Single(Infer("wat doet pane-9 nu")));
    }

    [Fact]
    public void TheTicketPrefixes_FallBackOnTheYouTrackLink_AsACommaList()
    {
        var linked = new Project("x", "X") { PluginFields = new Dictionary<string, string> { [Project.YouTrackProjectKey] = "EWB, AT-, EJ" } };

        Assert.Equal(["EWB", "AT", "EJ"], linked.EffectiveTicketPrefixes);
        Assert.Equal(["OWN"], (linked with { TicketPrefixes = ["OWN"] }).EffectiveTicketPrefixes);
    }

    // The session applies it only to a prompt put nowhere: one typed in a thread stays in that thread.
    [Fact]
    public void ThePromptIsFiledByWhatItNames_OnlyWhenTheOperatorPutItNowhere()
    {
        var vm = TestSessions.Pane(Substitute.For<ISessionManager>());
        vm.Threads = new ThreadLookup(id => id, _ => null, _ => null) { Infer = Infer };

        var (ids, sources) = vm.ProjectsForPrompt("voor DEP-422 de route en voor de cockpit de test");
        Assert.Equal(["depot", "cockpit"], ids);
        Assert.Equal(ProjectSource.Mentioned, sources!["cockpit"]);

        vm.ShownThread = "vov";
        Assert.Equal(["vov"], vm.ProjectsForPrompt("voor DEP-422 de route").Ids);
    }
}
