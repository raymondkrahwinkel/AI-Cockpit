using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using k8s;
using Cockpit.Plugin.Kubernetes.Helm;
using Cockpit.Plugin.Kubernetes.Model;

namespace Cockpit.Plugin.Kubernetes.Mcp;

// AC-1061 phase 6: upgrade a release. helm renders the chart (`--dry-run=server -o json`) because nothing in .NET
// renders Go templates faithfully; the rendered manifest is then diffed against the one the release secret holds,
// and that single computation is both what the operator approves and what gets applied.
internal sealed partial class KubernetesMcpTools
{
    private static readonly TimeSpan HelmRenderTimeout = TimeSpan.FromMinutes(5);

    [McpServerTool(Name = "helm_upgrade")]
    [Description("""
        Upgrades an existing Helm release: helm renders the chart with `--dry-run=server` (nothing is written by that run), the rendered manifest is diffed against the manifest the release secret currently holds, and the operator approves that literal diff before anything is applied. Only a release that already exists can be upgraded — there is no helm_install and no helm_uninstall. `chart` is a local chart path or a reference helm can already resolve (an OCI ref, or a repo the machine has); this tool does not add repositories or log in to registries. Values given in `values` (YAML) are merged over the release's current values by default; set reuseValues to false to start from the chart's defaults instead, which drops anything the operator set earlier. Values are passed to helm on stdin, never written to a file. A cluster registered with a pasted kubeconfig is refused: the CLI needs a kubeconfig path.

        This is NOT full helm parity, and the difference can matter in production. A real `helm upgrade` does a three-way merge over the old manifest, the new manifest and the live state, and runs the chart's hooks. This applies the rendered manifest as a JSON merge patch per resource: (1) each resource is written with a server-side apply under helm's own field manager, so a field helm owned and the target manifest no longer sets is removed, and whole resources the target manifest no longer has are deleted; (2) the apply is never forced — a field another controller has taken over is reported as a conflict for that resource instead of being seized; (3) an immutable field the apiserver refuses is reported as a failure for that resource — nothing is force-recreated; (4) pre/post-upgrade hooks are NOT run; (5) there is no transaction — every resource is attempted, and an upgrade that partially failed is recorded as a failed revision listing what did and did not apply. Anything this refuses can still be done with helm itself.

        Bookkeeping follows helm: a NEW revision is written carrying the rendered manifest and the resolved values, and the revision that was deployed is superseded. When the render changes no resource at all, nothing is applied and no revision is written — a values-only change that renders identically is reported, not recorded. helm has no fine-grained exit codes, so the reason given for a failed helm run is matched from its stderr text and is a hint, not a classification; the raw stderr comes with it.
        """)]
    public async Task<string> HelmUpgrade(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace the release lives in.")] string @namespace,
        [Description("The Helm release name, as listed by helm_list. It must already exist.")] string release,
        [Description("The chart: a local path, or a reference helm can resolve without adding a repository (e.g. an OCI ref).")] string chart,
        [Description("The chart version to upgrade to, or blank for whatever the reference resolves to.")] string chartVersion = "",
        [Description("Values as YAML, merged over the release's current values. Blank keeps the current values as they are.")] string values = "",
        [Description("Merge values over the release's current ones (default). False starts from the chart defaults, dropping values the operator set earlier.")] bool reuseValues = true,
        CancellationToken cancellationToken = default)
    {
        var (registration, clusterError) = _ResolveCluster(cluster);
        if (registration is null)
        {
            return clusterError!;
        }

        if (_ValidateUpgrade(release, chart, chartVersion) is { } invalid)
        {
            return invalid;
        }

        var (command, commandError) = HelmCommand.Build(
            _HelmExecutablePath(), registration, @namespace, "upgrade",
            _RenderArguments(release, chart, chartVersion, values, reuseValues),
            string.IsNullOrWhiteSpace(values) ? null : values);
        if (command is null)
        {
            return McpText.Error(commandError!);
        }

        // Rendering reads the release's own state through helm, and the diff is built from the release secret —
        // the same credential material every other helm tool asks for before it reads it.
        var decision = await gate.AuthorizeSensitiveNamespacedReadAsync(registration, @namespace, $"render an upgrade of Helm release \"{release}\" in namespace \"{@namespace}\" and read its current manifest", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        var rendered = await _helm.RunAsync(command, HelmRenderTimeout, cancellationToken);
        if (!rendered.Succeeded)
        {
            return McpText.Error(HelmFailure.Describe(rendered, command.FileName));
        }

        if (JsonNode.Parse(rendered.Stdout) is not JsonObject target)
        {
            return McpText.Error("helm did not return a release document for the dry run, so there is nothing to compare or apply.");
        }

        return await _WithClient(registration, (client, token) => _UpgradeAsync(client, registration, session, @namespace, release, chart, chartVersion, target, token), cancellationToken);
    }

    private async Task<string> _UpgradeAsync(
        IKubernetes client, ClusterRegistration registration, string session, string @namespace, string release, string chart, string chartVersion,
        JsonObject target, CancellationToken cancellationToken)
    {
        var secrets = await client.CoreV1.ListNamespacedSecretAsync(@namespace, labelSelector: $"owner=helm,name={release}", fieldSelector: $"type={HelmReleaseLedger.SecretType}", cancellationToken: cancellationToken);
        if (secrets.Items.Count == 0)
        {
            return McpText.Error($"No Helm release \"{release}\" found in namespace \"{@namespace}\".");
        }

        var ordered = secrets.Items.OrderByDescending(HelmReleaseLedger.RevisionOf).ToList();
        var current = HelmReleaseSecretCodec.TryDecodeRaw(ordered[0], out var currentError);
        if (current is null)
        {
            return McpText.Error(currentError!);
        }

        var targetManifest = _Manifest(target);
        if (string.IsNullOrWhiteSpace(targetManifest))
        {
            return McpText.Error("helm's dry run rendered no manifest, so there is nothing to apply.");
        }

        var diff = ManifestDiff.Compute(_Manifest(current), targetManifest);
        if (diff.IsEmpty)
        {
            return McpText.Ok(new
            {
                ok = true,
                release,
                applied = false,
                note = $"This upgrade renders exactly what revision {HelmReleaseLedger.RevisionOf(ordered[0])} already has, so nothing was applied and no revision was written. A values change that does not change the rendered manifest is not recorded — do that one with helm itself.",
            });
        }

        var (plan, planError) = await HelmApplyPlan.ResolveAsync(client, diff, @namespace, registration.AllowClusterScoped, cancellationToken);
        if (plan is null)
        {
            return McpText.Error(planError!);
        }

        // AC-1062: the diff goes to the gate as separate lines so it can escape and join them itself, instead of
        // arriving as one block with the breaks already baked in as `\n`.
        var version = string.IsNullOrWhiteSpace(chartVersion) ? string.Empty : $" version {chartVersion}";
        var operation = $"upgrade Helm release \"{release}\" in namespace \"{@namespace}\" to chart \"{chart}\"{version}";
        var decision = await gate.AuthorizeNamespacedMutationAsync(registration, @namespace, operation, session, diff.ToConsentLines(MaxConsentDiffLength));
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        var outcome = await HelmRevisionWriter.CarryOutAsync(
            client, plan, @namespace, release, ordered, target, "Upgrade complete", HelmReleaseLedger.PendingUpgrade, cancellationToken);
        if (outcome.Error is { } settleError)
        {
            return McpText.Error(settleError);
        }

        return McpText.Ok(new
        {
            ok = outcome.Succeeded,
            release,
            applied = true,
            newRevision = outcome.NewRevision,
            status = outcome.Status,
            diff = diff.ToJson(),
            resources = outcome.Results,
            note = outcome.Succeeded
                ? "Hooks were not run and no three-way merge was done — see the tool description for what that leaves untouched."
                : "Partially applied: this upgrade is recorded as a failed revision. The resources listed with an error were not changed.",
        });
    }

    private static IReadOnlyList<string> _RenderArguments(string release, string chart, string chartVersion, string values, bool reuseValues)
    {
        var arguments = new List<string> { release, chart, "--dry-run=server", "--output", "json" };
        if (!string.IsNullOrWhiteSpace(chartVersion))
        {
            arguments.Add("--version");
            arguments.Add(chartVersion);
        }

        if (reuseValues)
        {
            arguments.Add("--reuse-values");
        }

        if (!string.IsNullOrWhiteSpace(values))
        {
            arguments.Add("-f");
            arguments.Add("-");
        }

        return arguments;
    }

    // A leading dash would make helm read the value as another flag, which is the one way an agent-supplied string
    // can still change what the command does even though it travels as argv.
    private static string? _ValidateUpgrade(string release, string chart, string chartVersion) =>
        string.IsNullOrWhiteSpace(release) || string.IsNullOrWhiteSpace(chart)
            ? McpText.Error("release and chart are required — helm_upgrade only upgrades a release that already exists.")
            : new[] { release, chart, chartVersion }.Any(value => value.StartsWith('-'))
                ? McpText.Error("release, chart and chartVersion must not start with \"-\".")
                : null;
}
