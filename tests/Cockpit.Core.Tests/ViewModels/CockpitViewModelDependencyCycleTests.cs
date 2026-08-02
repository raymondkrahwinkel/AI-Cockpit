using System.Reflection;
using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// <see cref="CockpitViewModel"/> must not depend on anything that is built from <see cref="CockpitViewModel"/>.
/// <para>
/// This is not a style rule. Several App services take the cockpit view model as a constructor argument — the agent
/// workspace gateway is one, and the dialog service's own docs explain why the worktrees and projects dialogs take
/// their view models as parameters instead. Adding one of those to this constructor closes a loop the container then
/// follows until the stack runs out, and a StackOverflowException does not fail a test: it takes the whole test host
/// process down, mid-run, with no failing test named. That is exactly what it did when AC-397 tried to inject
/// <c>IWorkspaceAgentGateway</c> here — 3 769 of 3 782 tests passed and the run was simply aborted.
/// </para>
/// <para>
/// So the check is on the shape rather than on a resolve: asking the container would reproduce the crash rather than
/// report it.
/// </para>
/// </summary>
public class CockpitViewModelDependencyCycleTests
{
    [Fact]
    public void NoConstructorParameter_IsATypeThatItselfNeedsTheCockpitViewModel()
    {
        var offenders = typeof(CockpitViewModel).Assembly.GetTypes()
            .Where(candidate => candidate is { IsClass: true, IsAbstract: false })
            .Where(_TakesTheCockpitViewModel)
            .SelectMany(builtFromCockpit => builtFromCockpit.GetInterfaces().Append(builtFromCockpit))
            .Distinct()
            .Where(_IsAskedForByCockpitViewModel)
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"CockpitViewModel asks for {string.Join(", ", offenders)}, which is built from CockpitViewModel. "
            + "The container follows that loop until the stack runs out, which crashes the test host rather than "
            + "failing anything. Hand the dependency in at the call site instead, the way ShowWorktreesDialogAsync does.");
    }

    private static bool _TakesTheCockpitViewModel(Type type) =>
        type.GetConstructors().Any(constructor =>
            constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(CockpitViewModel)));

    private static bool _IsAskedForByCockpitViewModel(Type dependency) =>
        typeof(CockpitViewModel).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == dependency));
}
