using Avalonia.Controls;
using Avalonia.Headless;
using AvaloniaEdit.Rendering;
using Cockpit.App.Views.Editor;
using Material.Icons.Avalonia;

namespace Cockpit.App.ViewTests;

// AC-1753: the live-preview Markdown editor draws over the file without ever changing it.
[Collection("avalonia")]
public sealed class MarkdownEditorTests
{
    // Layer 2, view: what goes in comes back out, whatever the editor drew in between; each line kind gets its own look,
    // what it cannot show is labelled, and a read-only key only asks.
    [Fact]
    public void LoadingAndAskingBack_GivesTheSameText_AndEachLineKind_IsDrawnItsWay() => HeadlessAvalonia.Run(() =>
    {
        _EachLineKind_IsDrawnItsWay_AndAKeyInTheReadOnlyView_OnlyAsksToEdit();
        string[] files =
        [
            "---\r\ntitle: Edge cases\r\ntags: [md]\r\n---\r\n# Title\r\n\r\nA footnote[^1] and **bold**.\r\n\r\n<details>\r\n<summary>HTML</summary>\r\n</details>\r\n\r\n"
                + "| A | B |\r\n|---|:-:|\r\n| 1 | 2 \\| 3 |\r\n\r\n```cs\r\nvar x = 1;\r\n```\r\n\r\n[^1]: The note.\r\n[ref]: https://example.com\r\n",
            "- [ ] mixed\nendings\r\nno newline at the end, trailing space \t ",
        ];
        foreach (var file in files)
        {
            var editor = new MarkdownEditor { Text = file };
            var window = new Window { Content = editor, Width = 800, Height = 900 };
            window.Show();
            try
            {
                foreach (var line in new[] { 1, 2, 15, 3 })
                {
                    editor.EditLine(Math.Min(line, editor.TextEditor.Document.LineCount));
                    window.UpdateLayout();
                }

                editor.ShowSource = true;
                window.UpdateLayout();
                editor.ShowSource = false;
                editor.IsReadOnly = true;
                window.UpdateLayout();

                Assert.Equal(file, editor.Text);
            }
            finally
            {
                window.Close();
            }
        }
    });

    private static void _EachLineKind_IsDrawnItsWay_AndAKeyInTheReadOnlyView_OnlyAsksToEdit()
    {
        const string File = "# Title\n- item\n- [x] done\n<div>\nraw\n</div>\n[^1]: note\nplain";
        var editor = new MarkdownEditor { Text = File };
        var window = new Window { Content = editor, Width = 800, Height = 600 };
        window.Show();
        try
        {
            editor.EditLine(8);
            window.UpdateLayout();
            var lines = editor.TextEditor.TextArea.TextView.VisualLines.ToDictionary(v => v.FirstDocumentLine.LineNumber);

            Assert.Equal(2, Assert.IsType<InlineObjectElement>(lines[1].Elements[0]).DocumentLength);
            Assert.Equal(24, lines[1].Elements[^1].TextRunProperties.FontRenderingEmSize);
            Assert.IsType<FormattedTextElement>(lines[2].Elements[0]);
            Assert.IsType<MaterialIcon>(Assert.IsType<InlineObjectElement>(lines[3].Elements[0]).Element);
            Assert.All(lines[4].Elements, element =>
            {
                Assert.IsType<VisualLineText>(element);
                Assert.Contains("monospace", element.TextRunProperties.Typeface.FontFamily.ToString());
            });
            Assert.Equal("HTML — kept as text", editor.KeptAsTextLabel(4));
            Assert.Null(editor.KeptAsTextLabel(5));
            Assert.Equal("footnote — kept as text", editor.KeptAsTextLabel(7));

            var requests = new List<(int Line, string Text)>();
            editor.EditRequested += (line, text) => requests.Add((line, text));
            editor.IsReadOnly = true;
            editor.MarkLine(5);
            editor.TextEditor.TextArea.Focus();
            window.KeyTextInput("x");

            Assert.Equal([(5, "x")], requests);
            Assert.Equal(File, editor.Text);
        }
        finally
        {
            window.Close();
        }
    }
}
