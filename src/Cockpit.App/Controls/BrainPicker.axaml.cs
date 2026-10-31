using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

// AC-1992: lists the brains the moment the picker is shown for its editor, not when the editor is built: a profile
// list builds an editor per profile, and only the one on screen should ask a server anything.
public partial class BrainPicker : UserControl
{
    public BrainPicker()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _LoadOnce();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree())
        {
            _LoadOnce();
        }
    }

    private void _LoadOnce()
    {
        if (DataContext is BrainPickerViewModel { IsAvailable: true, HasLoaded: false, IsLoading: false } picker)
        {
            picker.RefreshCommand.Execute(null);
        }
    }
}
