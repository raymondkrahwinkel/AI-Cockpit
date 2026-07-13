using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace NodeEditorSpike;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        var drawing = WorkflowGraph.Build();
        var canvas = this.FindControl<Control>("Drawing");
        if (canvas is not null)
        {
            canvas.DataContext = drawing;
        }

        // The spike's own verdict, on screen: what the model round-trips to and back.
        var status = this.FindControl<TextBlock>("Status");
        if (status is not null)
        {
            status.Text = GraphRoundTrip.Describe(drawing);
        }
    }
}
