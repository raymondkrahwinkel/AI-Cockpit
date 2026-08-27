namespace Cockpit.App.ViewModels;

// One line of the resource panel (#78): a session, what it is using, and how much of the cockpit's total that is.
public sealed record ResourceRowViewModel(string Title, string Cpu, string Memory, double MemoryShare);
