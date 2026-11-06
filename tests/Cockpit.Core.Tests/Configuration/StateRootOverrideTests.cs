using System.Reflection;
using Cockpit.Core.Configuration;

namespace Cockpit.Core.Tests.Configuration;

/// <summary>
/// The state-root override (AC-1214), and above all its completeness: not that <c>cockpit.json</c> moves, but that
/// nothing stays behind.
/// </summary>
/// <remarks>
/// A half-isolated instance is more misleading than one that never claimed to be isolated — it reads as separate
/// while one path still writes into the operator's real state, and nobody finds out until the state is already
/// mixed. So the sweep below is deliberately not a list of the paths anyone remembered: it reflects over both
/// assemblies that resolve storage, and a path added later is covered without this file being edited.
/// </remarks>
[Collection(StateRootOverrideTests.Alone)]
public sealed class StateRootOverrideTests
{
    // The override is process-wide state, so nothing else may be reading a cockpit path while these run.
    public const string Alone = "state-root-override";

    private static readonly string Override =
        Path.Combine(Path.GetTempPath(), "cockpit-state-root-override-test");

    private const string DoesNotMoveByDesign = "CockpitBuild.DefaultStateRoot";

    // Scratch fallbacks follow TMP, so a worktree below StateRoot must not make them appear persistent.
    private static readonly string[] ExternalPaths =
    [
        "SessionScratch.FallbackBase",
        "SessionScratch.LegacyRoot",
    ];

    [Fact]
    public void ARelativeOverride_IsRefusedRatherThanResolvedPerProcess()
    {
        using var _ = new OverrideScope(Path.Combine("relative", "state"));

        var failure = Assert.Throws<InvalidOperationException>(() => CockpitBuild.StateRoot);

        Assert.Contains(CockpitBuild.StateRootVariable, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The completeness control. Every static path the two storage-resolving assemblies expose must move with the
    /// override — configuration, logs, plugins, worktrees, clones, audit trails, caches and the rest.
    /// </summary>
    [Fact]
    public void EveryPathTheAppResolves_MovesWithTheOverride()
    {
        var withoutOverride = _ResolvedPaths();

        // Nothing is proven by a sweep that found nothing: the properties must actually have been reachable.
        Assert.NotEmpty(withoutOverride);

        var underRealRoot = withoutOverride
            .Where(path => _IsUnder(path.Value, CockpitBuild.StateRoot))
            .ToList();

        Assert.NotEmpty(underRealRoot);

        // The exemption is only honoured while it names something that exists, so deleting or renaming that
        // property leaves a dead exemption rather than a quietly widened one.
        Assert.Contains(underRealRoot, path => path.Name == DoesNotMoveByDesign);

        underRealRoot = underRealRoot.Where(path => path.Name != DoesNotMoveByDesign && !ExternalPaths.Contains(path.Name)).ToList();

        using var _ = new OverrideScope(Override);

        var stragglers = underRealRoot
            .Select(path => (path.Name, Value: _Resolve(path.Member)))
            .Where(path => path.Value is null || !_IsUnder(path.Value, Override))
            .Select(path => $"{path.Name} = {path.Value ?? "<unreadable>"}")
            .ToList();

        Assert.True(
            stragglers.Count == 0,
            $"These paths did not follow {CockpitBuild.StateRootVariable} and would still write into the real "
            + $"state root:{Environment.NewLine}{string.Join(Environment.NewLine, stragglers)}");
    }

    // Every static property in the two assemblies that owns storage, read once. Reflection rather than a written
    // list on purpose: a list is a sample, and a sample is what this test exists to refuse.
    private static List<(string Name, PropertyInfo Member, string Value)> _ResolvedPaths()
    {
        var assemblies = new[]
        {
            typeof(CockpitBuild).Assembly,
            typeof(Cockpit.Infrastructure.Configuration.CockpitConfigPath).Assembly,
        };

        return assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(property => property.PropertyType == typeof(string) && property.GetMethod is not null)
            .Select(property => (Name: $"{property.DeclaringType!.Name}.{property.Name}", Member: property, Value: _Resolve(property)))
            .Where(path => path.Value is not null && Path.IsPathFullyQualified(path.Value))
            .Select(path => (path.Name, path.Member, Value: path.Value!))
            .ToList();
    }

    // A getter that throws or needs a configured app is not a path this test can judge; the source guard above is
    // what keeps such a property from being where a bypass hides.
    private static string? _Resolve(PropertyInfo property)
    {
        try
        {
            return property.GetValue(null) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool _IsUnder(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, root, StringComparison.OrdinalIgnoreCase);

    // Sets the variable for the length of a test and puts back whatever the machine had, so a run leaves the
    // environment it borrowed exactly as it found it.
    private sealed class OverrideScope : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(CockpitBuild.StateRootVariable);

        public OverrideScope(string? value) =>
            Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, value);

        public void Dispose() =>
            Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, _previous);
    }
}

[CollectionDefinition(StateRootOverrideTests.Alone, DisableParallelization = true)]
public sealed class StateRootOverrideCollection;
