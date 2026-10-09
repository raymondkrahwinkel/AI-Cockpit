using Avalonia.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class UsageThresholdSection : UserControl
{
    public UsageThresholdSection()
    {
        InitializeComponent();
    }

    public UsageThresholdSection(IReadOnlyList<UsageThresholdProviderViewModel> sessions, IReadOnlyList<UsageThresholdProviderViewModel> assistant)
        : this()
    {
        SessionGroups.Children.AddRange(sessions.Select(group => new UsageThresholdGroup(group)));
        AssistantGroups.Children.AddRange(assistant.Select(group => new UsageThresholdGroup(group)));
        AssistantBlock.IsVisible = assistant.Count > 0;
    }
}
