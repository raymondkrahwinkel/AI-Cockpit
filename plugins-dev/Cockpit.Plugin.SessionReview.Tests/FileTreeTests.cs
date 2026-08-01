namespace Cockpit.Plugin.SessionReview.Tests;

/// <summary>The shape of the review panel's file tree (AC-578): nesting, folder collapsing and ordering.</summary>
public class FileTreeTests
{
    private static FileDiff File(string path) => new(path, FileChangeKind.Modified, []);

    [Fact]
    public void Build_NestsFilesUnderTheirFolders()
    {
        var nodes = FileTree.Build([File("src/App.cs"), File("README.md")]);

        Assert.Equal(["src", "README.md"], nodes.Select(n => n.Label));
        Assert.Equal("App.cs", Assert.Single(nodes[0].Children).Label);
        Assert.Null(nodes[0].File);
        Assert.NotNull(nodes[1].File);
    }

    [Fact]
    public void Build_CollapsesAChainOfFoldersThatHoldsNothingElse()
    {
        // Without this a .NET repository spends four rows on empty levels before the first file appears.
        var nodes = FileTree.Build([File("src/Cockpit.App/Controls/Bar.axaml.cs")]);

        var only = Assert.Single(nodes);
        Assert.Equal("src/Cockpit.App/Controls", only.Label);
        Assert.Equal("Bar.axaml.cs", Assert.Single(only.Children).Label);
    }

    [Fact]
    public void Build_StopsCollapsingWhereTheTreeActuallyBranches()
    {
        var nodes = FileTree.Build([File("src/a/One.cs"), File("src/b/Two.cs")]);

        var root = Assert.Single(nodes);
        Assert.Equal("src", root.Label);
        Assert.Equal(["a", "b"], root.Children.Select(n => n.Label));
    }

    [Fact]
    public void Build_StopsCollapsingAtAFolderThatAlsoHoldsAFile()
    {
        var nodes = FileTree.Build([File("src/One.cs"), File("src/deep/Two.cs")]);

        var root = Assert.Single(nodes);
        Assert.Equal("src", root.Label);
        Assert.Equal(["deep", "One.cs"], root.Children.Select(n => n.Label));
    }

    [Fact]
    public void Build_PutsFoldersBeforeFilesAndSortsBothByName()
    {
        var nodes = FileTree.Build([File("zeta.md"), File("alpha.md"), File("lib/x.cs")]);

        Assert.Equal(["lib", "alpha.md", "zeta.md"], nodes.Select(n => n.Label));
    }

    [Fact]
    public void Build_CarriesTheFileItselfSoTheTreeCanShowItsCounts()
    {
        var file = new FileDiff("src/A.cs", FileChangeKind.Added, [new DiffRow(DiffLineKind.Added, null, 1, "x")]);

        var node = Assert.Single(Assert.Single(FileTree.Build([file])).Children);

        Assert.Same(file, node.File);
        Assert.Equal(1, node.File!.Added);
    }

    [Fact]
    public void Build_ReturnsNothingForNoChanges()
    {
        Assert.Empty(FileTree.Build([]));
    }
}
