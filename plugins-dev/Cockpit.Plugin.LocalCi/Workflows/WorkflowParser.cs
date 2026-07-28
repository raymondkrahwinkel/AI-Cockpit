using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Cockpit.Plugin.LocalCi.Workflows;

/// <summary>
/// Turns workflow YAML into a <see cref="WorkflowDocument"/>. Reads the document tree rather than deserialising
/// into a fixed shape, because the whole point of the classification downstream is to notice the keys we have no
/// shape for — a deserialiser would drop them and the job would look simpler than it is.
/// </summary>
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

        if (_Child(root, "jobs") is not { } jobsNode)
        {
            return WorkflowParseResult.Failed(path, "This file has no jobs: block, so there is nothing to run.");
        }

        if (jobsNode is not YamlMappingNode jobs)
        {
            return WorkflowParseResult.Failed(path, "The jobs: block is not a mapping of job names.");
        }

        var name = _Scalar(root, "name") ?? Path.GetFileName(path);
        var parsed = jobs.Children
            .Where(entry => entry.Key is YamlScalarNode { Value: not null })
            .Select(entry => _ReadJob(((YamlScalarNode)entry.Key).Value!, entry.Value))
            .ToList();

        return WorkflowParseResult.Parsed(new WorkflowDocument(path, name, parsed));
    }

    private static WorkflowJob _ReadJob(string id, YamlNode node)
    {
        if (node is not YamlMappingNode job)
        {
            // A job that is not a mapping carries no keys we can read; the classifier refuses it on the missing
            // runs-on rather than this parser inventing a verdict.
            return new WorkflowJob(id, null, RunsOnSpec.Missing, HasMatrix: false, [], []);
        }

        var keys = job.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value ?? string.Empty).ToList();
        var steps = _Child(job, "steps") is YamlSequenceNode sequence
            ? sequence.Children.Select(_ReadStep).ToList()
            : [];

        return new WorkflowJob(
            id,
            _Scalar(job, "name"),
            _ReadRunsOn(_Child(job, "runs-on")),
            _Child(job, "strategy") is YamlMappingNode strategy && _Child(strategy, "matrix") is not null,
            keys,
            steps);
    }

    private static WorkflowStep _ReadStep(YamlNode node)
    {
        if (node is not YamlMappingNode step)
        {
            return new WorkflowStep([], null);
        }

        var keys = step.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value ?? string.Empty).ToList();
        return new WorkflowStep(keys, _Scalar(step, "uses"));
    }

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
