namespace Cockpit.Core.Help;

// One search result. `Section` is what makes the hit land where the answer is instead of at the top of a
// long page — the same section ids the deep links use, so the two features share one mechanism rather than
// growing two addressing schemes that can disagree.
public sealed record HelpSearchHit(HelpArticle Article, HelpSection? Section, string Snippet, int Score)
{
    public HelpAddress Address => new(Article.Id, Section?.Id);
}
