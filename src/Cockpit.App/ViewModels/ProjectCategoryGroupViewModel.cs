namespace Cockpit.App.ViewModels;

// One category's cards in the Projects workspace list (AC-618) — "Privé", "Werk", or the ever-present "Uncategorized"
// catch-all, which is why it is nullable rather than reusing an empty string for "no heading at all": a null
// `CategoryName` is the single group an install where nobody uses categories gets, and it draws with no heading
public sealed record ProjectCategoryGroupViewModel(string? CategoryName, IReadOnlyList<ProjectCardViewModel> Cards)
{
    public bool HasHeader => CategoryName is { Length: > 0 };
}
