using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Core.Tests.Projects;

/// <summary>
/// The one invariant the opt-in rests on (AC-486): a row only sends its content along when it is an
/// <see cref="ProjectResourceRole.Instructions"/> row that was ticked. The flag and the role are two fields that
/// can disagree — nothing stops a hand-edited <c>cockpit.json</c> from setting the tick on a Memory row, where the
/// editor shows no checkbox to contradict it — and every reader that consulted only the flag was then a way in.
/// <para>
/// Its own file rather than an addition to <c>ProjectResourceTests</c>: that one still asserts through
/// FluentAssertions, and the standing rule is that a file this change opens gets converted with it (AC-372). Forty
/// eight conversions do not belong in a feature branch, and a new file adopts the dependency not at all.
/// </para>
/// </summary>
public class ProjectResourceSendsContentTests
{
    [Theory]
    [InlineData(ProjectResourceRole.Memory)]
    [InlineData(ProjectResourceRole.Reference)]
    public void SendsContent_StoredOnARowThatIsNotInstructions_ReadsAsFalse(ProjectResourceRole role)
    {
        var resource = new ProjectResource("/conventions.md", role) { SendsContent = true };

        Assert.False(resource.SendsContent);
    }

    [Fact]
    public void SendsContent_OnAnInstructionsRow_IsReportedAsStored()
    {
        var resource = new ProjectResource("/conventions.md", ProjectResourceRole.Instructions) { SendsContent = true };

        Assert.True(resource.SendsContent);
    }

    /// <summary>
    /// The path the review round actually measured: the tick is stored on a Memory row, the operator later changes
    /// that row's role to Instructions for reasons of their own, and the checkbox arrives already ticked in front
    /// of someone who never touched it — after which the file is opened on every session start. Asserted through
    /// the stored form rather than by constructing the record directly, because storage is where the two fields
    /// come to disagree in the first place.
    /// </summary>
    [Fact]
    public void ARowStoredAsMemoryWithTheTick_DoesNotBecomeTickedByChangingItsRole()
    {
        var stored = new ProjectResourceEntry
        {
            Reference = "/conventions.md",
            Role = nameof(ProjectResourceRole.Memory),
            SendsContent = true,
        };

        var loaded = stored.ToDomain();
        Assert.False(loaded.SendsContent, "a Memory row cannot carry a tick the editor never offered");

        var recategorised = loaded with { Role = ProjectResourceRole.Instructions };
        Assert.False(
            recategorised.SendsContent,
            "changing the role must not hand the row a tick nobody set — that is the whole of the opt-in");
    }
}
