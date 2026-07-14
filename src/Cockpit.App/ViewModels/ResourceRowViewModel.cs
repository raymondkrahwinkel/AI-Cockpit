namespace Cockpit.App.ViewModels;

/// <summary>
/// One line of the resource panel (#78): a session, what it is using, and how much of the cockpit's total that is.
/// <para>
/// <see cref="MemoryShare"/> is what the bar behind the figures is as wide as. A number tells you a session is using
/// 700 MB; the bar tells you it is most of what the whole app is using, which is the thing you would act on — and it
/// is the difference between a panel you read and a panel you glance at.
/// </para>
/// </summary>
/// <param name="Title">The session's own name, as it appears in the sidebar.</param>
/// <param name="Cpu">Its CPU, already written out ("CPU 4%").</param>
/// <param name="Memory">Its memory, already written out ("712 MB").</param>
/// <param name="MemoryShare">Its share of the cockpit's total memory, 0 to 1.</param>
public sealed record ResourceRowViewModel(string Title, string Cpu, string Memory, double MemoryShare);
