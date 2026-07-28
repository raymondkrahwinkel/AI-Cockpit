using Cockpit.Plugin.LocalCi.Workflows;

namespace Cockpit.Plugin.LocalCi.Tests;

public class WorkflowCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "local-ci-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProjectWithoutWorkflows_IsAnEmptyAnswerNotAFailure()
    {
        Directory.CreateDirectory(_root);

        Assert.Empty(WorkflowCatalog.ReadProject(_root));
    }

    [Fact]
    public void BothYamlExtensionsAreRead_InFileNameOrder()
    {
        _Write("second.yaml", "jobs:\n  a:\n    runs-on: ubuntu-latest\n");
        _Write("first.yml", "jobs:\n  a:\n    runs-on: ubuntu-latest\n");
        _Write("notes.md", "not a workflow");

        var found = WorkflowCatalog.ReadProject(_root);

        Assert.Equal(["first.yml", "second.yaml"], found.Select(w => Path.GetFileName(w.Path)));
    }

    [Fact]
    public void AWorkflowThatDoesNotParse_TravelsAlongsideTheOnesThatDo()
    {
        _Write("good.yml", "jobs:\n  a:\n    runs-on: ubuntu-latest\n");
        _Write("zbroken.yml", "jobs:\n  build:\n   - this: [is\n");

        var found = WorkflowCatalog.ReadProject(_root);

        Assert.True(found[0].IsParsed);
        Assert.False(found[1].IsParsed);
        Assert.NotNull(found[1].Error);
    }

    private void _Write(string fileName, string content)
    {
        var directory = Path.Combine(_root, ".github", "workflows");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
