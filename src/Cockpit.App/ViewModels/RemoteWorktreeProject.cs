namespace Cockpit.App.ViewModels;

public sealed record RemoteWorktreeProject(string Id, string Name)
{
    public override string ToString() => Name;
}
