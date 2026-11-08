using Cockpit.Core.Abstractions.Mentions;

namespace Cockpit.App.ViewModels;

// An accepted mention as it currently stands in the text: [Start..Start+Length) includes the '@'.
public sealed record MentionToken(MentionItem Item, int Start, int Length);
