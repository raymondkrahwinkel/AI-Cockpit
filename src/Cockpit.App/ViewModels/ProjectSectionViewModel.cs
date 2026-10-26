using System.Collections.ObjectModel;

namespace Cockpit.App.ViewModels;

public sealed class ProjectSectionViewModel : ViewModelBase
{
    private bool _isCollapsed;

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

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set => SetProperty(ref _isCollapsed, value);
    }

    public string? Notice { get; set; }

    public string? Error { get; set; }

    public int HereCount { get; set; }

    public int ToAddCount { get; set; }

    public int HiddenCount { get; set; }

    public int ShownCount { get; set; }

    public int TotalCount => HereCount + ToAddCount + HiddenCount;

    public ObservableCollection<ProjectListItemViewModel> Items { get; } = [];

    public string? OrganizationRole { get; set; }

    // AC-1600: "◆ Synvolution" or "● This machine"; the host follows it on every server section (Raymond, 26-10), so two
    // servers with an organisation of the same name read as two sections.
    public string Title => IsThisMachine ? "● This machine" : $"◆ {Name}";

    public bool HasHost => Host.Length > 0;

    public string HostLabel => $"· {Host}";

    public string Summary
    {
        get
        {
            var here = Items.Count(item => item.IsOnThisMachine);
            var toAdd = Items.Count - here;
            var parts = new List<string>
            {
                IsThisMachine ? "Not in Depot · only on this machine" : OrganizationSlug.Length > 0 ? "Organisation" : "Depot",
            };
            if (OrganizationRole is { Length: > 0 } role)
            {
                parts.Add($"you're {role}");
            }

            parts.Add($"{here} here");
            if (toAdd > 0)
            {
                parts.Add($"{toAdd} to add");
            }

            if (HiddenCount > 0)
            {
                parts.Add($"{HiddenCount} hidden");
            }

            if (ShownCount + HiddenCount < TotalCount)
            {
                parts.Add($"{ShownCount} of {TotalCount} shown");
            }

            return string.Join(" · ", parts);
        }
    }

    public bool HasError => Error is { Length: > 0 };

    public bool HasNotice => !HasError && Notice is { Length: > 0 };

    // A failed read and a stale "last known list" both offer another try; a section that read fine does not.
    public bool ShowsTryAgain => HasError || HasNotice;
}
