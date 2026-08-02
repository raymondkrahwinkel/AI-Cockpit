namespace Cockpit.App.ViewModels;

/// <summary>
/// One category's cards in the Projects workspace list (AC-618) — "Privé", "Werk", or the ever-present
/// "Uncategorized" catch-all, which is why it is nullable rather than reusing an empty string for "no heading at
/// all": a null <see cref="CategoryName"/> is the single group an install where nobody uses categories gets, and it
/// draws with no heading whatsoever — the list looks exactly as it did before this existed. Once at least one
/// project carries a category, every real category becomes its own group in <c>ProjectSettings.CategoryOrder</c>'s
/// order, and "Uncategorized" is always the last group, even with zero cards in it right now — it never disappears
/// the way a real category does once its last project lets go of it.
/// </summary>
public sealed record ProjectCategoryGroupViewModel(string? CategoryName, IReadOnlyList<ProjectCardViewModel> Cards)
{
    public bool HasHeader => CategoryName is { Length: > 0 };
}
