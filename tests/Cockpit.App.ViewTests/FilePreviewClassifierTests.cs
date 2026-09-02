using System.Text;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The soort-choice per file kind (AC-642) — FilePreviewClassifier. One behaviour, two tables: the extension
/// decides, and where it does not the first bytes do. FilePreviewKind is internal, so it cannot stand in a public
/// signature (CS0051): the rows carry it boxed and the parameter is object, which keeps the enum member's own
/// name in the case report.
/// </summary>
public class FilePreviewClassifierTests
{
    public static IEnumerable<object[]> Extensions() =>
    [
        ["photo.png", FilePreviewKind.Image],
        ["photo.JPG", FilePreviewKind.Image],
        ["photo.jpeg", FilePreviewKind.Image],
        ["photo.gif", FilePreviewKind.Image],
        ["photo.bmp", FilePreviewKind.Image],
        ["photo.webp", FilePreviewKind.Image],
        ["icon.svg", FilePreviewKind.Svg],
        ["Notes.md", FilePreviewKind.Markdown],
        ["data.json", FilePreviewKind.Json],
        ["data.csv", FilePreviewKind.Csv],
        ["data.tsv", FilePreviewKind.Csv],
    ];

    [Theory]
    [MemberData(nameof(Extensions))]
    public void TheExtensionNamesTheKind(string path, object expected)
    {
        Assert.Equal(expected, FilePreviewClassifier.Classify(path, []));
    }

    public static IEnumerable<object[]> Heads()
    {
        // A generic binary is caught by its NUL byte; the same head under a .pdf name is a PDF anyway (AC-730),
        // which is the pair that says the extension is read before the content is sniffed at all.
        byte[] pdfHead = [0x25, 0x50, 0x44, 0x46, 0x00, 0x01];

        yield return ["MarkdownView.cs", Encoding.UTF8.GetBytes("public sealed class Foo { }"), FilePreviewKind.Text];
        yield return ["empty.log", Array.Empty<byte>(), FilePreviewKind.Text];
        yield return ["report.bin", pdfHead, FilePreviewKind.Other];
        yield return ["report.pdf", pdfHead, FilePreviewKind.Pdf];
        // 0xFF is not valid anywhere in UTF-8, and there is no NUL byte to catch it by.
        yield return ["data.bin", new byte[] { 0xFF, 0xFE, 0x01, 0x02 }, FilePreviewKind.Other];
    }

    [Theory]
    [MemberData(nameof(Heads))]
    public void WithoutAKindOfItsOwn_TheFirstBytesDecide(string path, byte[] head, object expected)
    {
        Assert.Equal(expected, FilePreviewClassifier.Classify(path, head));
    }
}
