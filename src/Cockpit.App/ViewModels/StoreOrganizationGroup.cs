namespace Cockpit.App.ViewModels;

// One organization's plugins on the Discover page, as the "From <organization>" block above the public list.
public sealed record StoreOrganizationGroup(string Organization, IReadOnlyList<StorePluginRowViewModel> Plugins)
{
    public string Header => $"From {Organization}";
}
