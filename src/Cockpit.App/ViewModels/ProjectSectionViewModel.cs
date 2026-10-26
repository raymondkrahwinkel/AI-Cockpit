using System.Collections.ObjectModel;

namespace Cockpit.App.ViewModels;

public sealed class ProjectSectionViewModel
{
    public ProjectSectionViewModel(string key, string sourceKey, string organizationSlug, string name, string host, bool isThisMachine)
    {
        Key = key;
        SourceKey = sourceKey;
        OrganizationSlug = organizationSlug;
        Name = name;
        Host = host;
        IsThisMachine = isThisMachine;
    }

    public string Key { get; }

    public string SourceKey { get; }

    public string OrganizationSlug { get; }

    public string Name { get; }

    public string Host { get; }

    public bool IsThisMachine { get; }

    public bool IsCollapsed { get; set; }

    public string? Notice { get; set; }

    public string? Error { get; set; }

    public int HereCount { get; set; }

    public int ToAddCount { get; set; }

    public int HiddenCount { get; set; }

    public int ShownCount { get; set; }

    public int TotalCount => HereCount + ToAddCount + HiddenCount;

    public ObservableCollection<ProjectListItemViewModel> Items { get; } = [];
}
