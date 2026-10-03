using System.Reflection;
using System.Text.RegularExpressions;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests.Architecture;

public sealed class FrontendBoundaryTests
{
    [Fact]
    public void FrontendCode_OnlyUsesFrontendContracts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appRoot = Path.Combine(repositoryRoot, "src", "Cockpit.App");
        var forbiddenNames = new[]
        {
            "ISessionManager",
            "ISessionRuntime",
            "ISessionDriver",
            "ISessionTurnGate",
            nameof(SessionEvent),
        }
        .Concat(typeof(SessionEvent).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(SessionEvent)))
            .Select(type => type.Name));
        // AC-1449: a member that shares an event's name (`TranscriptEntryKind.TurnCompleted`) is no dependency on it;
        // a name qualified by its namespace (`Core.Sessions.SessionError`) still is.
        var notAMember = @"(?:(?<!\.)|(?<=Sessions\.))";

        // AC-1441: a declaration that shares a name is none either. An enum member alone on its line (`Question,`, a
        // last `TurnCompleted`, `Question = 3`), but not a type in a list (`Func<SessionEvent, bool>`) or an arm (`=>`).
        var enumMember = @"(?<=^[ \t]*\w+)[ \t]*(?:[,}\r\n]|=(?!>))";

        // A property named like one (`public string Question { get; }`).
        var propertyName = @"\s*\{\s*get";
        var names = string.Join("|", forbiddenNames.Select(Regex.Escape));
        var forbiddenReference = new Regex(
            $@"{notAMember}\b(?:{names})\b(?!{enumMember})(?!{propertyName})|\bSessionHost\b",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var commentLine = new Regex(@"^\s*//.*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        // AC-1441: a fully qualified name reaches into Infrastructure as surely as a using does.
        var infrastructureReference = new Regex(@"\bCockpit\.Infrastructure\b", RegexOptions.CultureInvariant);
        // AC-1441: Services too. What may name the backend's implementation is the composition root: Program,
        // App.axaml.cs, DependencyInjection and the Composition folder, which the second half below keeps thin.
        var violations = new[] { "ViewModels", "Views", "Services" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(appRoot, directory),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(path =>
            {
                var code = commentLine.Replace(File.ReadAllText(path), string.Empty);
                return infrastructureReference.IsMatch(code) || forbiddenReference.IsMatch(code);
            })
            .Select(path => Path.GetRelativePath(appRoot, path).Replace('\\', '/'))
            .Order()
            .ToArray();

        // The exemption is no back door: a Composition type is internal; a class is a sealed adapter with no public
        // surface beyond the contracts it implements, a static class keeps no state. Logic belongs elsewhere.
        var markers = new[] { typeof(Cockpit.Core.Abstractions.ISingletonService), typeof(IDisposable), typeof(IAsyncDisposable) };
        var composition = typeof(Cockpit.App.Composition.DesktopComposition).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith("Cockpit.App.Composition", StringComparison.Ordinal) == true
                && !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false));
        var thick = composition
            .Where(type =>
            {
                if (type.IsPublic || type.IsNestedPublic)
                {
                    return true;
                }

                if (type.IsNested)
                {
                    return false;
                }

                if (type is { IsAbstract: true, IsSealed: true })
                {
                    return type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        .Any(field => !field.IsInitOnly && !field.IsLiteral);
                }

                var contracts = type.GetInterfaces().Except(markers).ToArray();
                var contractMembers = contracts.SelectMany(contract => type.GetInterfaceMap(contract).TargetMethods).ToHashSet();
                return !type.IsSealed
                    || contracts.Length == 0
                    || type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                        .Any(method => !contractMembers.Contains(method));
            })
            .Select(type => type.Name)
            .Order()
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Frontend boundary violation: {string.Join(", ", violations)}");
        Assert.True(thick.Length == 0, $"Composition holds more than wiring: {string.Join(", ", thick)}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cockpit.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
