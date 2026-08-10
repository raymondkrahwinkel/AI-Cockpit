using System.Text.Json.Nodes;
using Cockpit.Core.Projects;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Projects;

namespace Cockpit.Core.Tests.Projects;

/// <summary>
/// The claim AC-318 rests on: a credential in a project information row is found by the same traversal that encrypts
/// every other secret in the settings, and by the scrubber that keeps them out of backups.
/// <para>
/// End to end rather than per component, because the claim spans two of them and each is individually convincing while
/// the pair could still fail. The walker has to reach a field nested two arrays deep — <c>Projects[i].AdditionalInfo[j]</c>
/// — which is deeper than any secret the settings held before, and the name rule has to accept <c>SecretValue</c>. Both
/// hold; this pins them together so a change to either is caught here rather than by an operator with a leaked token.
/// </para>
/// </summary>
public class ProjectSecretRowReachesTheEncryptionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public ProjectSecretRowReachesTheEncryptionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task ASecretRowWrittenByTheStore_IsFoundByTheSecretTraversal()
    {
        const string credential = "row-credential-that-must-not-survive";
        var project = Project.Create("Cockpit") with
        {
            AdditionalInfo =
            [
                new ProjectInfoField("Repository", "https://github.com/example/repo"),
                new ProjectInfoField("Deploy token", credential) { IsSecret = true },
            ],
        };

        await new ProjectStore(_configFilePath).SaveAsync(ProjectSettings.Empty.WithProject(project));
        var config = JsonNode.Parse(await File.ReadAllTextAsync(_configFilePath))!;

        // Exactly what the encryption layer and the backup scrubber both do to the settings.
        var rewritten = SecretJsonWalker.Transform(config, SecretFields.ByName, (_, _) => "REDACTED");

        var path = Assert.Single(rewritten);
        Assert.Contains("AdditionalInfo", path);

        var scrubbed = config.ToJsonString();
        Assert.DoesNotContain(credential, scrubbed);
        Assert.Contains("https://github.com/example/repo", scrubbed);
    }

    // AC-607: the project password itself is cached locally the same way as any other credential (AC-353's
    // heuristic, not a new storage mechanism) — its property name alone ("ProjectPassword") is what routes it
    // through the same SecretJsonWalker traversal already proven above for AdditionalInfo rows.
    [Fact]
    public async Task AProjectPassword_IsFoundByTheSecretTraversal()
    {
        const string password = "project-password-that-must-not-survive";
        var project = Project.Create("Cockpit") with { ProjectPassword = password };

        await new ProjectStore(_configFilePath).SaveAsync(ProjectSettings.Empty.WithProject(project));
        var config = JsonNode.Parse(await File.ReadAllTextAsync(_configFilePath))!;

        var rewritten = SecretJsonWalker.Transform(config, SecretFields.ByName, (_, _) => "REDACTED");

        var path = Assert.Single(rewritten);
        Assert.Contains("ProjectPassword", path);

        var scrubbed = config.ToJsonString();
        Assert.DoesNotContain(password, scrubbed);
    }

    // AC-607 review finding 2: Project is a record, whose compiler-generated ToString() would otherwise print
    // every public property, ProjectPassword included, in the clear the moment anyone logs or debug-prints one.
    [Fact]
    public void ToString_WithAProjectPasswordSet_NeverContainsItInTheClear()
    {
        const string password = "project-password-that-must-not-appear-in-tostring";
        var project = Project.Create("Cockpit") with { ProjectPassword = password };

        Assert.DoesNotContain(password, $"{project}", StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrdinaryRow_IsNotTreatedAsASecret()
    {
        // The mirror of the test above: were Value itself secret-named, every row would be encrypted and the config
        // would stop being the operator's to read.
        var project = Project.Create("Cockpit") with
        {
            AdditionalInfo = [new ProjectInfoField("Customer", "Acme BV")],
        };

        await new ProjectStore(_configFilePath).SaveAsync(ProjectSettings.Empty.WithProject(project));
        var config = JsonNode.Parse(await File.ReadAllTextAsync(_configFilePath))!;

        Assert.Empty(SecretJsonWalker.Transform(config, SecretFields.ByName, (_, _) => "REDACTED"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
