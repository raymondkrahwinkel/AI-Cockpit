using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Core.Tests.Projects;

/// <summary>
/// AC-1071 acceptance criterion 7: before this ticket the persona lived in <c>BehaviorPrompt</c>, which travels
/// with a shared project. Reading a pre-assistant entry lifts it out — but only when the whole field is that
/// sentence. The data below is what Raymond's own <c>cockpit.json</c> actually held on 2026-08-25.
/// </summary>
public class ProjectEntryAssistantMigrationTests
{
    private static ProjectEntry Entry(string? behaviorPrompt, string? assistant = null) =>
        new() { Id = "p1", Name = "Cockpit", BehaviorPrompt = behaviorPrompt, Assistant = assistant };

    /// <summary>
    /// The one mixed prompt on that machine: a persona sentence followed by 289 characters of real branch and
    /// deploy conventions. Guessing where one ends would throw those away, so nothing is touched at all.
    /// </summary>
    [Fact]
    public void ToDomain_ABehaviorPromptThatOnlyOpensWithAPersona_IsLeftExactlyAsItIs()
    {
        const string mixed = "Gebruik Aura. Werk standaard op branch `net9-upgrade`, niet op `master` — die is dood "
            + "sinds 2023 en staat nog op .NET 7, maar is wel de GitHub-default, dus een verse clone landt op de "
            + "verkeerde tak. Deploy loopt via `release`; alleen daar draait CI (dotnet-windows.yml, bouwt MSIX "
            + "voor de scanzuilen).";

        var project = Entry(mixed).ToDomain();

        Assert.Null(project.Assistant);
        Assert.Equal(mixed, project.BehaviorPrompt);
    }

    /// <summary>
    /// An entry that already answers for itself is never second-guessed — this is what makes reading twice, or
    /// reading an already-saved file, change nothing.
    /// </summary>
    [Fact]
    public void ToDomain_AnEntryThatAlreadyNamesAnAssistant_KeepsBothFieldsUntouched()
    {
        var project = Entry("Gebruik Zyra", assistant: "Vex").ToDomain();

        Assert.Equal("Vex", project.Assistant);
        Assert.Equal("Gebruik Zyra", project.BehaviorPrompt);
    }

    [Fact]
    public void ToDomain_ReadingAMigratedProjectBackAgain_ChangesNothingFurther()
    {
        var once = Entry("Gebruik Zyra").ToDomain();
        var twice = ProjectEntry.FromDomain(once).ToDomain();

        Assert.Equal("Zyra", twice.Assistant);
        Assert.Null(twice.BehaviorPrompt);
    }

}
