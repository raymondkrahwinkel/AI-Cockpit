namespace Cockpit.Core.Abstractions.Shell;

// AC-1335: the process an elevated command runs as. `CommandLine` is the ground truth the operator approves and
// the runner starts — one string, so the banner and the start cannot drift apart.
public sealed record ElevatedCommandPlan(string Executable, string Arguments, string OutputFile)
{
    public string CommandLine => $"\"{Executable}\" {Arguments}";
}
