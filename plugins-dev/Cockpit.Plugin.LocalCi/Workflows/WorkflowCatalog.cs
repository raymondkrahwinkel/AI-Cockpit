namespace Cockpit.Plugin.LocalCi.Workflows;

/// <summary>
/// Finds and reads a project's workflows. Takes the project root rather than asking the host for the session's
/// project, so the reading is testable against a directory on disk and stays usable from anywhere later.
/// </summary>
internal static class WorkflowCatalog
{
    private static readonly string WorkflowsDirectory = Path.Combine(".github", "workflows");

    /// <summary>
    /// Every workflow under <c>.github/workflows</c>, in file-name order, each either parsed or carrying the reason
    /// it could not be. A project without that directory yields an empty list — that is an answer, not a failure.
    /// </summary>
    public static IReadOnlyList<WorkflowParseResult> ReadProject(string projectRoot)
    {
        var directory = Path.Combine(projectRoot, WorkflowsDirectory);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory)
            .Where(file => Path.GetExtension(file) is ".yml" or ".yaml")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(_Read)
            .ToList();
    }

    private static WorkflowParseResult _Read(string path)
    {
        try
        {
            return WorkflowParser.Parse(path, File.ReadAllText(path));
        }
        catch (IOException exception)
        {
            return WorkflowParseResult.Failed(path, $"This file could not be read: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return WorkflowParseResult.Failed(path, $"This file could not be read: {exception.Message}");
        }
    }
}
