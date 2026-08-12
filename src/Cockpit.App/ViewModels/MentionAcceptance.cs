namespace Cockpit.App.ViewModels;

// What the composer needs to splice an accepted mention into the text: replace [TokenStart..caret] with '@' +
// Path + a trailing space.
public sealed record MentionAcceptance(int TokenStart, string Path);
