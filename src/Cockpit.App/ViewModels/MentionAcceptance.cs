using Cockpit.Core.Abstractions.Mentions;

namespace Cockpit.App.ViewModels;

// What the composer splices into the text: '@' + Item.InsertText + a trailing space, in place of [TokenStart..caret].
public sealed record MentionAcceptance(int TokenStart, MentionItem Item);
