using Avalonia.Controls;

namespace Cockpit.App.Views;

/// <summary>Modal dialog for editing the shared MCP-server registry (#26); its <see cref="ViewModels.McpServersViewModel"/> raises CloseRequested to dismiss it.</summary>
public partial class McpServersDialog : Window
{
    public McpServersDialog()
    {
        InitializeComponent();
    }
}
