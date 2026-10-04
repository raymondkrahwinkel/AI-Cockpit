using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using static Cockpit.Plugin.Autopilot.AutopilotChannelContract;

namespace Cockpit.Plugin.Autopilot;

// AC-1418: the backend half of Autopilot's channel. An attached workspace gets its runs here and a State event with
// the whole snapshot on every change; the operator's clicks come back as actions. Transport, plus the one publisher at
// a time that keeps a snapshot's seq in the order it was taken.
internal sealed class AutopilotChannel : IDisposable
{
    private readonly ICockpitHost _host;
    private readonly AutopilotSettings _settings;
    private readonly AutopilotPlanController _plan;
    private readonly AutopilotRunManager _manager;
    private readonly AutopilotRunQueue _queue;
    private readonly AutopilotRunHistory _history;
    private readonly AutopilotTemplateStore _templates;
    private readonly List<IDisposable> _handles = [];
    private readonly Dictionary<string, AutopilotWorkspaceRuns> _workspaces = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _publishes;

    public AutopilotChannel(ICockpitHost host, AutopilotSettings settings, AutopilotPlanController plan, AutopilotRunManager manager, AutopilotRunQueue queue, AutopilotRunHistory history, AutopilotTemplateStore templates)
    {
        _host = host;
        _settings = settings;
        _plan = plan;
        _manager = manager;
        _queue = queue;
        _history = history;
        _templates = templates;

        _plan.Changed += _OnPlanChanged;
        _manager.Changed += _PublishAll;
        _queue.Changed += _PublishAll;
        _history.Changed += _PublishAll;
        _templates.Changed += _OnTemplatesChanged;

        _Do<WorkspaceRef>(Attach, request => _Attach(request.WorkspaceId));
        _Do<WorkspaceRef>(Detach, request => _Detach(request.WorkspaceId));

        _On(BeginPlanning, _ => ToJson(_plan.BeginPlanning(AutopilotPlan.Empty(source: null, goal: string.Empty))));
        _Do(CancelPlanning, _CancelPlanning);
        _OnAsync(EmbedPlanningCeo, async payload =>
        {
            var request = Read<PlanningCeoRequest>(payload);
            return ToJson(_Runs(request.WorkspaceId) is { } runs ? await runs.EmbedPlanningCeoAsync(request.ActiveDirectory, request.KickoffMessage) : null);
        });
        _Do<WorkspaceRef>(ClosePlanningCeo, request => _Runs(request.WorkspaceId)?.ClosePlanningCeo());
        _On(Submit, payload => ToJson(_Submit(Read<SubmitRequest>(payload))));

        // Set the Stopped phase first, then cancel (order matters — see AutopilotPlanController.Stop): the driver settles
        // only when every step finished, never on a mid-run cancel, so the snapshot after teardown stays Stopped.
        _Do<RunRequest>(Stop, request =>
        {
            if (_Run(request.WorkspaceId, request.RunId) is { } context)
            {
                context.Controller.Stop("Stopped by operator");
                context.Cancel();
            }
        });
        _Do<RunRequest>(Intervene, request => _Run(request.WorkspaceId, request.RunId)?.Coordinator.EnableCurrentStepInput());
        _Do<AnswerRequest>(Answer, request => _ = _Run(request.WorkspaceId, request.RunId)?.Coordinator.AnswerBlockadeAsync(request.Text));
        _Do<MergeGoRequest>(MergeGo, request => _Run(request.WorkspaceId, request.RunId)?.Coordinator.ReportMergeGo(request.Issue, request.Go, request.Reason, "the operator"));
        _Do<MergeGoRequest>(EpicGo, request => _manager.ReportMergeGo(request.Issue, request.Go, request.Reason, "the operator"));

        _Do<QueueRequest>(QueueMoveUp, request => _queue.MoveUp(request.Index));
        _Do<QueueRequest>(QueueMoveDown, request => _queue.MoveDown(request.Index));
        _Do<QueueRequest>(QueueRemove, request => _queue.RemoveAt(request.Index));
        _Do(HistoryClear, _history.Clear);
        _Do<CorrectionRequest>(HistoryCorrect, _Correct);

        _On(Templates, _ => ToJson(_Catalog()));
        _Do<AutopilotTemplate>(TemplateUpsertUser, _templates.UpsertUserTemplate);
        _Do<AutopilotTemplateOverride>(TemplateUpsertOverride, _templates.UpsertOverride);
        _Do<string>(TemplateDelete, _templates.DeleteUserTemplate);
        _Do<string>(TemplateReset, _templates.ResetOverride);
        _On(Trackers, _ => ToJson(_host.TrackerProviders.Select(provider => provider.TrackerId).ToList()));
        _OnAsync(RememberedPaths, async _ => ToJson(await _host.GetRememberedWorkingPathsAsync()));
    }

    // The plugin is disabled or reloaded: every workspace's runs stop as they would on the workspace's own close.
    public void Dispose()
    {
        List<AutopilotWorkspaceRuns> workspaces;
        lock (_gate)
        {
            workspaces = [.. _workspaces.Values];
            _workspaces.Clear();
        }

        foreach (var runs in workspaces)
        {
            runs.Changed -= _PublishAll;
            runs.Close();
        }

        _plan.Changed -= _OnPlanChanged;
        _manager.Changed -= _PublishAll;
        _queue.Changed -= _PublishAll;
        _history.Changed -= _PublishAll;
        _templates.Changed -= _OnTemplatesChanged;
        foreach (var handle in _handles)
        {
            handle.Dispose();
        }
    }

    // A workspace opened: while it is open its runs are the manager's runner, so attaching starts any queued run.
    private void _Attach(string workspaceId)
    {
        // Checked and added under the gate, so two attaches build one; started outside it, since becoming the runner
        // pumps the queue under the manager's own lock.
        AutopilotWorkspaceRuns? added = null;
        lock (_gate)
        {
            if (!_workspaces.ContainsKey(workspaceId))
            {
                added = new AutopilotWorkspaceRuns(_host, workspaceId, _settings, _plan, _manager, _queue, _history);
                _workspaces[workspaceId] = added;
            }
        }

        if (added is not null)
        {
            added.Changed += _PublishAll;
            added.Start();
        }

        _PublishAll();
    }

    // The workspace was really closed (its tab dismissed, not a mere tab-switch): stop every run so none runs on
    // headless, and stop being the manager's runner so a queued run does not start with no surface.
    private void _Detach(string workspaceId)
    {
        AutopilotWorkspaceRuns? runs;
        lock (_gate)
        {
            if (_workspaces.Remove(workspaceId, out runs))
            {
                runs.Changed -= _PublishAll;
            }
        }

        runs?.Close();
    }

    private void _CancelPlanning()
    {
        if (_plan.Phase == AutopilotPlanPhase.Planning)
        {
            _plan.CancelPlanning();
        }
    }

    // Approve submits the round's draft to the run manager, which runs it now or queues it — not the planning
    // controller. A round without steps has nothing to run, and a draft the CEO changed since the operator saw it
    // is refused, so nothing is approved unseen.
    private bool _Submit(SubmitRequest request)
    {
        if (_plan.Plan is not { Steps.Count: > 0 } plan || !SameOnTheWire(plan, request.Shown))
        {
            return false;
        }

        var name = request.Name.Trim();
        var approved = string.IsNullOrEmpty(name) ? plan : plan.WithName(name);
        _manager.Submit(approved
            .WithWorkingDirectory(request.WorkingDirectory.Trim())
            .WithDeliversPullRequest(request.DeliversPullRequest)
            .WithMergeMode(request.MergeMode));
        return true;
    }

    // The reclassify menu (AC-347): sets CorrectionSource to Operator so a manual override stays visible as one. Found
    // by run and finish time rather than position, so an edit lands on the run it opened on even after another settled.
    private void _Correct(CorrectionRequest request)
    {
        var record = _history.Items.FirstOrDefault(candidate => candidate.RunId == request.RunId && candidate.FinishedAt == request.FinishedAt);
        if (record is null || request.StepIndex < 0 || request.StepIndex >= record.Steps.Count)
        {
            return;
        }

        var steps = record.Steps.ToList();
        steps[request.StepIndex] = steps[request.StepIndex] with { Correction = request.Kind, CorrectionSource = AutopilotCorrectionSource.Operator };
        _history.Replace(record, record with { Steps = steps });
    }

    // The template list the picker and the settings read (AC-189), with the readable names of the plugins that own one.
    private AutopilotTemplateCatalog _Catalog() => new(
        _templates.List(_host.RegisteredAutopilotTemplates),
        _host.InstalledPlugins.GroupBy(plugin => plugin.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.Ordinal));

    private AutopilotWorkspaceRuns? _Runs(string workspaceId)
    {
        lock (_gate)
        {
            return _workspaces.GetValueOrDefault(workspaceId);
        }
    }

    private AutopilotRunContext? _Run(string workspaceId, string runId) => _Runs(workspaceId)?.Find(runId);

    private void _OnPlanChanged(object? sender, EventArgs e) => _PublishAll();

    private void _OnTemplatesChanged() => _host.Channel.Publish(TemplatesChanged, ToJson(true));

    // One publisher at a time, so seq follows the order the snapshots were taken. A change during a publish never
    // waits — its thread may hold a lock the snapshot needs — it makes the running publisher take one more round.
    private void _PublishAll()
    {
        if (Interlocked.Increment(ref _publishes) > 1)
        {
            return;
        }

        try
        {
            do
            {
                List<AutopilotWorkspaceRuns> workspaces;
                lock (_gate)
                {
                    workspaces = [.. _workspaces.Values];
                }

                foreach (var runs in workspaces)
                {
                    _Publish(runs);
                }
            }
            while (Interlocked.Exchange(ref _publishes, 1) > 1 || Interlocked.Decrement(ref _publishes) > 0);
        }
        catch (Exception)
        {
            // A snapshot that failed must not leave every later change unpublished.
            Interlocked.Exchange(ref _publishes, 0);
            throw;
        }
    }

    private void _Publish(AutopilotWorkspaceRuns runs) => _host.Channel.Publish(State, ToJson(runs.Snapshot()));

    private void _On(string action, Func<JsonElement, JsonElement> handler) =>
        _handles.Add(_host.Channel.Handle(action, (payload, _) => Task.FromResult(handler(payload))));

    private void _OnAsync(string action, Func<JsonElement, Task<JsonElement>> handler) =>
        _handles.Add(_host.Channel.Handle(action, (payload, _) => handler(payload)));

    private void _Do<T>(string action, Action<T> handler) => _On(action, payload =>
    {
        handler(Read<T>(payload));
        return ToJson(true);
    });

    private void _Do(string action, Action handler) => _On(action, _ =>
    {
        handler();
        return ToJson(true);
    });
}
