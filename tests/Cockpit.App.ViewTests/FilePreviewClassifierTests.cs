using System.Text;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>The soort-choice per file kind (AC-642), one case per kind — <see cref="FilePreviewClassifier"/>.</summary>
public class FilePreviewClassifierTests
{
    [Theory]
    [InlineData("photo.png")]
    [InlineData("photo.JPG")]
    [InlineData("photo.jpeg")]
    [InlineData("photo.gif")]
    [InlineData("photo.bmp")]
    [InlineData("photo.webp")]
    public void ImageExtensions_ClassifyAsImage(string path)
    {
        Assert.Equal(FilePreviewKind.Image, FilePreviewClassifier.Classify(path, []));
    }

    [Fact]
    public void SvgExtension_ClassifiesAsSvg()
    {
        Assert.Equal(FilePreviewKind.Svg, FilePreviewClassifier.Classify("icon.svg", []));
    }

    [Fact]
    public void MarkdownExtension_ClassifiesAsMarkdown()
    {
        Assert.Equal(FilePreviewKind.Markdown, FilePreviewClassifier.Classify("Notes.md", []));
    }

    [Fact]
    public void JsonExtension_ClassifiesAsJson()
    {
        Assert.Equal(FilePreviewKind.Json, FilePreviewClassifier.Classify("data.json", []));
    }

    [Theory]
    [InlineData("data.csv")]
    [InlineData("data.tsv")]
    public void CsvAndTsvExtensions_ClassifyAsCsv(string path)
    {
        Assert.Equal(FilePreviewKind.Csv, FilePreviewClassifier.Classify(path, []));
    }

    [Fact]
    public void Utf8SourceFile_ClassifiesAsText()
    {
        var head = Encoding.UTF8.GetBytes("public sealed class Foo { }");
        Assert.Equal(FilePreviewKind.Text, FilePreviewClassifier.Classify("MarkdownView.cs", head));
    }

    [Fact]
    public void EmptyFile_ClassifiesAsText()
    {
        Assert.Equal(FilePreviewKind.Text, FilePreviewClassifier.Classify("empty.log", []));
    }

    [Fact]
    public void BinaryWithNulByte_ClassifiesAsOther()
    {
        byte[] head = [0x25, 0x50, 0x44, 0x46, 0x00, 0x01];
        Assert.Equal(FilePreviewKind.Other, FilePreviewClassifier.Classify("report.pdf", head));
    }

    [Fact]
    public void InvalidUtf8WithoutNulByte_ClassifiesAsOther()
    {
        // 0xFF is not valid anywhere in UTF-8.
        byte[] head = [0xFF, 0xFE, 0x01, 0x02];
        Assert.Equal(FilePreviewKind.Other, FilePreviewClassifier.Classify("data.bin", head));
    }
}
