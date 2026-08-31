using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Cockpit.Plugin.LocalCi.Workflows;

// Turns workflow YAML into a `WorkflowDocument`. Reads the document tree rather than deserialising
// into a fixed shape, because the whole point of the classification downstream is to notice the keys we have no
// shape for — a deserialiser would drop them and the job would look simpler than it is.
// Anything shaped in a way this reader has no reading for makes the whole file a reported failure rather than a
// quietly shorter job list. A job or a step that vanishes during parsing is worse than one that is refused: the
// refusal is on screen with a reason, while the disappearance leaves a list that looks complete and is not.
internal static class WorkflowParser
{
    public static WorkflowParseResult Parse(string path, string yaml)
    {
        YamlStream stream = new();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException exception)
        {
            return WorkflowParseResult.Failed(path, $"This file is not valid YAML: {exception.Message}");
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return WorkflowParseResult.Failed(path, "This file does not look like a workflow: it has no top-level mapping.");
        }

        if (stream.Documents.Count > 1)
        {
            // Reading only the first would drop the rest without a word — the same silent loss the job and step
            // shape checks below exist to prevent.
            return WorkflowParseResult.Failed(path, "This file holds more than one YAML document, and only one workflow can be read from it.");
        }

        if (_Child(root, "jobs") is not { } jobsNode)
        {
            return WorkflowParseResult.Failed(path, "This file has no jobs: block, so there is nothing to run.");
        }

        if (jobsNode is not YamlMappingNode jobs)
        {
            return WorkflowParseResult.Failed(path, "The jobs: block is not a mapping of job names.");
        }

        if (_ShapeProblem(jobs) is { } problem)
        {
            return WorkflowParseResult.Failed(path, problem);
        }

        var name = _Scalar(root, "name") ?? Path.GetFileName(path);
        var keys = root.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value ?? string.Empty).ToList();
        var parsed = jobs.Children
            .Select(entry => _ReadJob(((YamlScalarNode)entry.Key).Value!, entry.Value))
            .ToList();

        return WorkflowParseResult.Parsed(new WorkflowDocument(path, name, keys, parsed));
    }

    // The shapes the reader below assumes, checked once so that reading itself has no unreadable cases.
    private static string? _ShapeProblem(YamlMappingNode jobs)
    {
        if (jobs.Children.Keys.Any(key => key is not YamlScalarNode { Value: not null }))
        {
            return "This file has a job whose name is not a plain string, which cannot be read.";
        }

        foreach (var steps in jobs.Children.Values.OfType<YamlMappingNode>().Select(job => _Child(job, "steps")))
        {
            if (steps is null)
            {
                continue;
            }

            if (steps is not YamlSequenceNode sequence)
            {
                return "This file has a steps: that is not a list of steps, which cannot be read.";
            }

            if (sequence.Children.Any(step => step is not YamlMappingNode))
            {
                return "This file has a step that is not a mapping of keys, which cannot be read.";
            }
        }

        return null;
    }

    private static WorkflowJob _ReadJob(string id, YamlNode node)
    {
        if (node is not YamlMappingNode job)
        {
            // A job that is not a mapping carries no keys we can read; the classifier refuses it on the missing
            // runs-on rather than this parser inventing a verdict.
            return new WorkflowJob(id, null, RunsOnSpec.Missing, MatrixKind.None, [], [], []);
        }

        var keys = job.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value ?? string.Empty).ToList();
        var steps = _Child(job, "steps") is YamlSequenceNode sequence
            ? sequence.Children.OfType<YamlMappingNode>().Select(_ReadStep).ToList()
            : [];
        var strategy = _Child(job, "strategy") as YamlMappingNode;
        var strategyKeys = strategy is null
            ? []
            : strategy.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value ?? string.Empty).ToList();

        var matrix = _ReadMatrix(strategy is null ? null : _Child(strategy, "matrix"));
        return new WorkflowJob(id, _Scalar(job, "name"), _ReadRunsOn(_Child(job, "runs-on")), matrix, strategyKeys, keys, steps);
    }

    private static MatrixKind _ReadMatrix(YamlNode? node)
    {
        if (node is null)
        {
            return MatrixKind.None;
        }

        if (node is not YamlMappingNode { Children.Count: 1 } matrix)
        {
            return MatrixKind.Unsupported;
        }

        var axis = matrix.Children.Single();
        if (axis.Key is not YamlScalarNode { Value: { } axisName }
            || axisName.Equals("include", StringComparison.OrdinalIgnoreCase)
            || axisName.Equals("exclude", StringComparison.OrdinalIgnoreCase))
        {
            return MatrixKind.Unsupported;
        }

        var simpleValues = axis.Value is YamlSequenceNode { Children.Count: > 0 } values
            && values.Children.All(value => value is YamlScalarNode { Value: not null });
        var dynamicValues = axis.Value is YamlScalarNode { Value: { } expression }
            && Regex.IsMatch(
                expression,
                @"^\$\{\{\s*fromJSON\(needs\.[A-Za-z0-9_-]+\.outputs\.[A-Za-z0-9_-]+\)\s*\}\}$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return simpleValues || dynamicValues ? MatrixKind.Simple : MatrixKind.Unsupported;
    }

    private static WorkflowStep _ReadStep(YamlMappingNode step) =>
        new(step.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value ?? string.Empty).ToList(),
            _Scalar(step, "uses"),
            _Scalar(step, "if"));

    private static RunsOnSpec _ReadRunsOn(YamlNode? node) => node switch
    {
        null => RunsOnSpec.Missing,
        YamlScalarNode { Value: { } value } when value.Contains("${{", StringComparison.Ordinal) => RunsOnSpec.Expression,
        YamlScalarNode { Value: { } value } when !string.IsNullOrWhiteSpace(value) => RunsOnSpec.Named(value.Trim()),
        _ => RunsOnSpec.NotUnderstood,
    };

    private static YamlNode? _Child(YamlMappingNode node, string key) =>
        node.Children.FirstOrDefault(entry => entry.Key is YamlScalarNode scalar && scalar.Value == key).Value;

    private static string? _Scalar(YamlMappingNode node, string key) =>
        _Child(node, key) is YamlScalarNode { Value: { } value } ? value : null;
}
