using Cockpit.Plugin.Diagram.Wireframe;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

public sealed class WireframeCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wireframe-catalog-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Write_refuses_when_the_file_changed_underneath()
    {
        var path = WireframeCatalog.Create(Path.Combine(_root, "home"), "Scherm", "screen \"A\"");
        var asOpened = File.ReadAllText(path);
        File.WriteAllText(path, "# Scherm\n\n```wireframe\nscreen \"Elders gewijzigd\"\n```\n");

        Assert.Throws<IOException>(() => WireframeCatalog.Write(path, "Scherm", "screen \"A\"\n  button \"Ga\"", asOpened));
        Assert.Contains("Elders gewijzigd", File.ReadAllText(path));
    }
}
