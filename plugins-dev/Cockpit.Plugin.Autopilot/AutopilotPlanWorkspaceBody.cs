using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The CEO plan-flow workspace body (AC-174/AC-175): the pipeline as blocks on the left and, once a step is running,
/// its live session on the right. It renders whatever the shared <see cref="AutopilotPlanController"/> holds and
/// re-renders on its change — the planning pop-out and the executeStep session-embedding land as the flow is wired up.
/// This grows up alongside the shipped gate-based <see cref="AutopilotWorkspaceBody"/> rather than replacing it in one move.
/// </summary>
internal sealed class AutopilotPlanWorkspaceBody : UserControl
{
    private readonly ICockpitHost _host;
    private readonly IWorkspaceContext _context;
    private readonly AutopilotSettings _settings;
    private readonly AutopilotPlanController _plan;
    private readonly AutopilotRunManager _manager;
    private readonly AutopilotRunQueue _queue;
    private readonly AutopilotRunHistory _history;
    private readonly AutopilotTemplateStore _templates;
    private readonly List<AutopilotRunContext> _activeContexts = [];
    private readonly ContentControl _bodyHost = new();

    // The MCP surface the planning CEO is scoped to (AC-197): the plan-emit endpoint it uses to draft the plan, and
    // nothing else. Left on the request's default empty list it would inherit the host's entire selection (161 tools
    // observed) — every tool definition in its context (tokens), none of it needed to plan. The CEO tools endpoint
    // (autopilot_tracker_stage / autopilot_tracker_note) is deliberately NOT here (AC-212): that endpoint is only mounted
    // once a run is active (AutopilotPlugin gates it on manager.Active.Count > 0), so listing it during planning mounted
    // nothing and only made the CEO grab for tracker tools it never had and report them missing. Keeping the source issue
    // in sync is the run's job — the CEO validator (AutopilotValidatorBrief) plus the coordinator's auto-advance (AC-202),
    // both during execution — not the planning round's. So both a source-triggered and a CEO-first run plan on the plan
    // endpoint alone.
    internal static IReadOnlyList<string> PlanningCeoMcpServers(bool hasSource) =>
        [AutopilotPlanTools.EndpointName];

    private bool _popoutOpen;
    private int _completedRuns;
    private IEmbeddedSession? _ceo;

    public AutopilotPlanWorkspaceBody(ICockpitHost host, IWorkspaceContext context, AutopilotSettings settings, AutopilotPlanController plan, AutopilotRunManager manager, AutopilotRunQueue queue, AutopilotRunHistory history, AutopilotTemplateStore templates)
    {
        _host = host;
        _context = context;
        _settings = settings;
        _plan = plan;
        _manager = manager;
        _queue = queue;
        _history = history;
        _templates = templates;

        // While this workspace is open it is the manager's runner — a run embeds its sessions in this context — so setting
        // it starts any runs already queued, and clearing it on close stops runs from starting with no surface. The
        // manager, queue and history raise Changed as runs start/end/queue/settle, which re-renders the surface.
        _manager.Runner = _StartRun;
        _manager.Changed += _OnStateChanged;
        _queue.Changed += _OnStateChanged;
        _history.Changed += _OnStateChanged;

        // Stop every running run when this workspace is really closed (its tab dismissed, not a mere tab-switch) so none
        // keeps going headless with no surface to stop it (AC-174).
        _context.Closed += _OnWorkspaceClosed;

        var header = new Border
        {
            Padding = new Thickness(16, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            [DockPanel.DockProperty] = Dock.Top,
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = "Autopilot", FontWeight = FontWeight.SemiBold, FontSize = 15 },
                    new TextBlock
                    {
                        Text = "The CEO plans the work, you approve it once, then it runs autonomously to a merge-ready PR.",
                        Opacity = 0.7,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = _Brush("CockpitTextSecondaryBrush"),
                    },
                },
            },
        };

        Content = new DockPanel { LastChildFill = true, Children = { header, _bodyHost } };
        _Render();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _plan.Changed += _OnChanged;
        _Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _plan.Changed -= _OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    // The workspace was really closed (WorkspacesViewModel raised it on tab-dismiss, on the UI thread): stop every
    // running run so none runs on headless, and stop being the manager's runner so a queued run does not start with no
    // surface. Cancelling a run unwinds its driver loop and coordinator awaits, closing its step sessions and CEO.
    private void _OnWorkspaceClosed(object? sender, EventArgs e)
    {
        _manager.Runner = null;
        _manager.Changed -= _OnStateChanged;
        _queue.Changed -= _OnStateChanged;
        _history.Changed -= _OnStateChanged;
        foreach (var context in _activeContexts.ToList())
        {
            context.Cancel();
        }
    }

    // The manager or queue changed (a run started/ended/queued) — re-render on the UI thread.
    private void _OnStateChanged() => _OnUi(_Render);

    private void _OnChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _Render();
        }
        else
        {
            Dispatcher.UIThread.Post(_Render);
        }
    }

    private void _Render()
    {
        // Open the planning pop-out once per round, not per plan edit: the CEO re-emitting the plan makes a new
        // AutopilotPlan instance, so keying on the instance reopened the pop-out — embedding a fresh, empty CEO session
        // and wiping the planning chat — on every emit. A flag, cleared when the pop-out closes, opens it exactly once.
        if (_plan.Phase == AutopilotPlanPhase.Planning && _plan.Plan is not null && !_popoutOpen)
        {
            _popoutOpen = true;
            Dispatcher.UIThread.Post(() => _ = _ShowPlanningPopoutAsync());
        }

        _bodyHost.Content = _BuildSurface();
    }

    // The run surface (AC-174): a bar with New run and the queued runs on top, the settled-run history at the bottom, and
    // the running run's pipeline filling between them. The first running run is shown in full; any others (with a
    // concurrency cap above one) are listed on the bar.
    private Control _BuildSurface()
    {
        var surface = new DockPanel { LastChildFill = true };
        var bar = _BuildQueueBar();
        DockPanel.SetDock(bar, Dock.Top);
        surface.Children.Add(bar);

        // History docks at the bottom (Raymond 2026-07-22), under the running run, so a settled run that left the live
        // surface is still visible — what it was and how it ended — rather than vanishing. Only shown once there is any.
        if (_history.Count > 0)
        {
            var historyPanel = _BuildHistorySection();
            DockPanel.SetDock(historyPanel, Dock.Bottom);
            surface.Children.Add(historyPanel);
        }

        surface.Children.Add(_activeContexts.Count > 0
            ? _BuildPipeline(_activeContexts[0])
            : _CentredHint(MaterialIconKind.RobotOutline, "No run is executing", "Start one with New run, or queue several — they run one after another, up to the concurrency you set."));

        return surface;
    }

    // The history section (Raymond 2026-07-22): a header with a Clear, then the settled runs newest-first in a bounded
    // scroll — each row its name, outcome and step summary — so the operator sees what has run without it cluttering the
    // live pipeline. Capped in height so a long history does not push the running run off-screen.
    private Control _BuildHistorySection()
    {
        var clear = new Button
        {
            Content = "Clear",
            Padding = new Thickness(9, 3),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            [DockPanel.DockProperty] = Dock.Right,
        };
        clear.Click += (_, _) => _history.Clear();

        var header = new DockPanel
        {
            LastChildFill = false,
            Children =
            {
                clear,
                new TextBlock
                {
                    Text = $"History · {_history.Count}",
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = _Brush("CockpitTextSecondaryBrush"),
                    [DockPanel.DockProperty] = Dock.Left,
                },
            },
        };

        var list = new StackPanel { Spacing = 0 };
        foreach (var record in _history.Items)
        {
            list.Children.Add(_BuildHistoryRow(record));
        }

        return new Border
        {
            MaxHeight = 240,
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Background = _Brush("CockpitSecondaryBgBrush"),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    new Border { Padding = new Thickness(12, 7), [DockPanel.DockProperty] = Dock.Top, Child = header },
                    new ScrollViewer { Content = list },
                },
            },
        };
    }

    // One settled run in history: an outcome dot, its name and finish time, the outcome line (flagging any failed steps
    // even on a merge-ready run so a green dot never hides them), and each step with its mark and — for a step that
    // failed or was blocked — the reason it carried, so history explains why a step did not pass, not just that it did
    // not (Raymond 2026-07-22).
    private Control _BuildHistoryRow(AutopilotRunRecord record)
    {
        var mergeReady = record.Outcome == AutopilotPlanPhase.MergeReady;
        var stopped = record.Outcome == AutopilotPlanPhase.Stopped;
        var failedCount = record.Steps.Count(step => step.Status is AutopilotStepStatus.Failed);
        var clean = mergeReady && failedCount == 0;

        // A clean merge-ready is green; a run the operator stopped is neutral grey (not a failure, a deliberate stop,
        // AC-196); a merge-ready that still had failed (optional) steps, or a blocked run, is amber — so a run with
        // failures never reads as an unqualified success.
        var outcomeBrush = clean
            ? _Brush("CockpitStatusDoneBrush")
            : stopped
                ? _Brush("CockpitTextSecondaryBrush")
                : _Brush("CockpitStatusWaitingBrush");

        var dot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 10, 0),
            [DockPanel.DockProperty] = Dock.Left,
            Background = outcomeBrush,
        };

        var title = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                new TextBlock
                {
                    Text = _FormatFinishedAt(record.FinishedAt),
                    FontSize = 10.5,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = _Brush("CockpitTextFaintBrush"),
                    [DockPanel.DockProperty] = Dock.Right,
                },
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(record.Label) ? "(untitled run)" : record.Label,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = _Brush("CockpitTextPrimaryBrush"),
                },
            },
        };

        var outcomeText = stopped
            ? "Stopped"
            : mergeReady
                ? failedCount > 0 ? $"Merge-ready · {failedCount} optional step(s) failed" : "Merge-ready"
                : $"Blocked — {record.BlockReason}";

        var meta = new StackPanel { Spacing = 2, Children = { title } };
        meta.Children.Add(new TextBlock
        {
            Text = outcomeText,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = outcomeBrush,
        });

        // Each step on its own line with a mark, and — where it failed or was blocked — the reason it carried underneath,
        // so the operator sees why without reopening the run.
        foreach (var step in record.Steps)
        {
            meta.Children.Add(new TextBlock
            {
                Text = $"{_HistoryStepMark(step.Status)}  {step.Title}",
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = step.Status is AutopilotStepStatus.Failed ? _Brush("CockpitStatusErrorBrush") : _Brush("CockpitTextSecondaryBrush"),
            });

            if (step.Status is AutopilotStepStatus.Failed or AutopilotStepStatus.Blocked && !string.IsNullOrWhiteSpace(step.Note))
            {
                meta.Children.Add(new TextBlock
                {
                    Text = step.Note,
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16, 0, 0, 0),
                    Foreground = _Brush("CockpitTextFaintBrush"),
                });
            }
        }

        return new Border
        {
            Padding = new Thickness(12, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Child = new DockPanel { LastChildFill = true, Children = { dot, meta } },
        };
    }

    private static string _HistoryStepMark(AutopilotStepStatus status) => status switch
    {
        AutopilotStepStatus.Passed => "✓",
        AutopilotStepStatus.Failed => "✗",
        AutopilotStepStatus.Skipped => "–",
        AutopilotStepStatus.Blocked => "⏸",
        _ => "·",
    };

    // The finish time as a short, local, human string — parsed from the stored ISO stamp; the raw stamp is the fallback
    // if it somehow does not parse, so a row never shows blank.
    private static string _FormatFinishedAt(string iso) =>
        DateTimeOffset.TryParse(iso, out var when) ? when.LocalDateTime.ToString("d MMM HH:mm") : iso;

    // The bar above the run: a New-run button, and the queued runs (with the running count) that the operator can
    // reorder or drop before they run.
    private Control _BuildQueueBar()
    {
        var newRun = new Button
        {
            Content = "+ New run",
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(6),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            [DockPanel.DockProperty] = Dock.Left,
        };
        newRun.Click += (_, _) => _StartPlanningRound();

        var running = _activeContexts.Count;
        var summary = new TextBlock
        {
            Text = running switch
            {
                0 when _queue.Count == 0 => "No runs queued.",
                _ => $"{running} running · {_queue.Count} queued",
            },
            FontSize = 11,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
            [DockPanel.DockProperty] = Dock.Left,
        };

        var headRow = new DockPanel { LastChildFill = false, Children = { newRun, summary } };

        // The persistent "needs you" marker (AC-203): while any active run sits in AwaitingOperator it stands here on the
        // queue bar, so it appears whichever run or the history the operator is looking at. Added docked Right on the same
        // row as New run / the summary — see _BuildNeedsYouBadge for why it persists and why a consult never trips it.
        var awaiting = _activeContexts.Count(context => context.Controller.Phase == AutopilotPlanPhase.AwaitingOperator);
        if (NeedsOperatorAttention(_activeContexts.Select(context => context.Controller.Phase)))
        {
            headRow.Children.Add(_BuildNeedsYouBadge(awaiting));
        }

        var head = new Border
        {
            Padding = new Thickness(12, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            [DockPanel.DockProperty] = Dock.Top,
            Child = headRow,
        };

        if (_queue.Count == 0)
        {
            return head;
        }

        var list = new StackPanel { Spacing = 0 };
        for (var index = 0; index < _queue.Items.Count; index++)
        {
            list.Children.Add(_BuildQueueRow(index, _queue.Items[index]));
        }

        return new DockPanel { LastChildFill = false, Children = { head, new Border { [DockPanel.DockProperty] = Dock.Top, Child = list } } };
    }

    // Whether the surface needs the operator (AC-203): true while any active run sits in AwaitingOperator — a step hit a
    // blockade only the operator can answer (AC-155). Pure so the persistent "needs you" marker's condition is unit-testable
    // without a host or a UI thread. Keyed on the phase alone, so a CEO consult — which keeps the run Running (spoor 2,
    // AC-201) — never trips it; only a real operator escalation (spoor 3, AwaitingOperator) does.
    internal static bool NeedsOperatorAttention(IEnumerable<AutopilotPlanPhase> activePhases) =>
        activePhases.Any(phase => phase == AutopilotPlanPhase.AwaitingOperator);

    // The persistent "needs you" badge (AC-203): a standing amber marker on the queue bar, which docks at the top of the
    // surface and never scrolls away with a specific run's view or the history. It is rebuilt from the live phase on every
    // render — a run entering the wait raises Changed, which re-renders the surface — so it appears the moment a run turns
    // AwaitingOperator and disappears as soon as the operator answers (→ Running) or the run settles. This is the lasting
    // signal for an operator who missed the one-shot AC-194 toast and is not looking at the blocked run; the toast stays as
    // the in-the-moment nudge, this outlives it.
    private Control _BuildNeedsYouBadge(int count)
    {
        var onWaiting = new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x0E));
        return new Border
        {
            [DockPanel.DockProperty] = Dock.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Background = _Brush("CockpitStatusWaitingBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 3),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new MaterialIcon
                    {
                        Kind = MaterialIconKind.AlertCircleOutline,
                        Width = 14,
                        Height = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = onWaiting,
                    },
                    new TextBlock
                    {
                        Text = count > 1 ? $"{count} runs need you" : "Needs you",
                        FontSize = 11,
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = onWaiting,
                    },
                },
            },
        };
    }

    // One queued run: its goal, and controls to move it up/down or drop it before it runs.
    private Control _BuildQueueRow(int index, AutopilotPlan plan)
    {
        var goal = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(plan.Label) ? "(untitled run)" : plan.Label,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _Brush("CockpitTextPrimaryBrush"),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, [DockPanel.DockProperty] = Dock.Right };
        buttons.Children.Add(_QueueButton("↑", () => _queue.MoveUp(index)));
        buttons.Children.Add(_QueueButton("↓", () => _queue.MoveDown(index)));
        buttons.Children.Add(_QueueButton("✕", () => _queue.RemoveAt(index)));

        return new Border
        {
            Padding = new Thickness(12, 6),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Background = _Brush("CockpitSecondaryBgBrush"),
            Child = new DockPanel { LastChildFill = true, Children = { buttons, goal } },
        };
    }

    private Button _QueueButton(string glyph, Action onClick)
    {
        var button = new Button { Content = glyph, Padding = new Thickness(7, 2), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => onClick();
        return button;
    }

    // New run: open a fresh planning round on the planning controller. Guarded on the pop-out already being open (not the
    // phase — the planning controller idles in Planning, so a phase guard would block every New run); a run already in
    // flight is unaffected, since planning is decoupled from executing. BeginPlanning turns the phase Planning and sets a
    // fresh empty draft, which the render turns into an opened pop-out.
    private void _StartPlanningRound()
    {
        // A planning round needs a CEO profile — the guard moved here from the side-menu button (Raymond 2026-07-22) so
        // that button only opens the workspace, and the profile is required at the point a run is actually planned,
        // whichever entry point starts it.
        if (_popoutOpen || !_RequireCeoProfile())
        {
            return;
        }

        _plan.BeginPlanning(AutopilotPlan.Empty(source: null, goal: string.Empty));
    }

    // A planning round needs a CEO profile: without one the host falls back to whatever the first configured profile is,
    // which may be a local model that cannot plan. Rather than plan a round that quietly misbehaves, tell the operator
    // and offer the settings. Returns whether a profile is set.
    private bool _RequireCeoProfile()
    {
        if (!string.IsNullOrWhiteSpace(_settings.CeoProfileLabel()))
        {
            return true;
        }

        _host.ShowToast(
            "Set a CEO profile in the Autopilot settings before planning.",
            PluginToastSeverity.Warning,
            "Open settings",
            () => _ = _host.ShowSettingsAsync());
        return false;
    }

    // The manager's runner (AC-174): start a run for a dequeued plan in its own context, track it for the surface, and
    // hand the manager the coordinator and completion task. Removing it from the surface when it settles is marshalled to
    // the UI thread, since the run task can complete off it.
    private AutopilotRunHandle _StartRun(AutopilotPlan plan)
    {
        var context = new AutopilotRunContext(_host, _context, _settings, plan, _RunOnUiAsync);
        _ = _RunOnUiAsync(() =>
        {
            _activeContexts.Add(context);
            context.Changed += _OnStateChanged;
            _Render();
        });
        _ = _RemoveWhenDoneAsync(context);
        return new AutopilotRunHandle(context.Coordinator, context.Completed);
    }

    private async Task _RemoveWhenDoneAsync(AutopilotRunContext context)
    {
        try
        {
            await context.Completed;
        }
        catch (Exception)
        {
            // The run settled or died; either way drop it from the surface.
        }

        // Snapshot the settled run off the controller before dropping it, so history and the toast read a coherent state.
        var controller = context.Controller;
        var settledPlan = controller.Plan;
        var outcome = controller.Phase;
        var blockReason = controller.BlockReason;

        _OnUi(() =>
        {
            _activeContexts.Remove(context);
            context.Changed -= _OnStateChanged;
            _RecordAndNotify(settledPlan, outcome, blockReason);
            _Render();
        });
    }

    // A run finished: record it in history (unless it was cancelled — a closed workspace, still Running — where there is
    // nothing to record) and raise a toast (Raymond 2026-07-22). Every settled run gets its own done/blocked toast; when
    // that empties both the running set and the queue after more than one run, an extra "whole queue finished" summary
    // follows, so a single run is one toast and a staged queue ends with a clear all-done.
    // Whether a run's final phase is one that gets recorded in history and toasted (AC-196): merge-ready, blocked, or
    // operator-stopped. A run that is still Running when its context is dropped was cancelled by a closed workspace and
    // has nothing to record. Pure so the "a stopped run is recorded, not silently dropped" rule is unit-testable.
    internal static bool IsSettledOutcome(AutopilotPlanPhase outcome) =>
        outcome is AutopilotPlanPhase.MergeReady or AutopilotPlanPhase.Blocked or AutopilotPlanPhase.Stopped;

    private void _RecordAndNotify(AutopilotPlan? plan, AutopilotPlanPhase outcome, string? blockReason)
    {
        // A run counts as settled — recorded and toasted — when it merged-ready, blocked, or the operator stopped it
        // (AC-196). A run still Running is a cancelled/closed workspace with nothing to record.
        if (IsSettledOutcome(outcome) && plan is not null)
        {
            _completedRuns++;
            _history.Add(new AutopilotRunRecord(
                plan.Name,
                plan.Goal,
                outcome,
                blockReason,
                DateTimeOffset.Now.ToString("o"),
                [.. plan.Steps.Select(step => new AutopilotRunStepRecord(step.Title, step.Status, step.Note))]));

            var label = string.IsNullOrWhiteSpace(plan.Label) ? "Autopilot run" : plan.Label;
            switch (outcome)
            {
                case AutopilotPlanPhase.MergeReady:
                    _host.ShowToast($"Run “{label}” is merge-ready.", PluginToastSeverity.Success);
                    break;
                case AutopilotPlanPhase.Stopped:
                    _host.ShowToast($"Run “{label}” stopped.", PluginToastSeverity.Information);
                    break;
                default:
                    _host.ShowToast($"Run “{label}” is blocked — {blockReason}", PluginToastSeverity.Warning);
                    break;
            }
        }

        // The whole queue drained: after a staged batch (more than one run), a single summary that it is all done. A lone
        // run needs no summary — its own toast above already said it finished.
        if (_activeContexts.Count == 0 && _queue.Count == 0)
        {
            if (_completedRuns >= 2)
            {
                _host.ShowToast($"All queued Autopilot runs finished ({_completedRuns}).", PluginToastSeverity.Information);
            }

            _completedRuns = 0;
        }
    }

    // The planning pop-out (AC-174/AC-175): the draft plan on the left updating live as the CEO revises it, the CEO's
    // own session on the right, and one Approve that freezes the plan and starts the run. The CEO session is embedded
    // through the workspace context (AC-122) and shown in the dialog; it is closed when the pop-out closes.
    private async Task _ShowPlanningPopoutAsync()
    {
        try
        {
            // The template choice (AC-189, slice 3): before the CEO session starts — its kickoff is fixed at embed — let
            // the operator pick a template or plan free. A chosen template's resolved body becomes the CEO's kickoff
            // instead of the hardcoded source kickoff; "free" keeps the current behaviour exactly. Cancelling the picker
            // backs out of the whole round, the same as closing the pop-out unapproved.
            var pick = await _PickTemplateAsync(_plan.Plan?.Source);
            if (pick.Cancelled)
            {
                _popoutOpen = false;
                if (_plan.Phase == AutopilotPlanPhase.Planning)
                {
                    _plan.CancelPlanning();
                }

                return;
            }

            var kickoff = AutopilotTemplateKickoff.Build(pick.Template, _plan.Plan?.Source);
            if (pick.Template is not null && kickoff.MissingPlaceholders.Count > 0)
            {
                // The resolver never fails on an unfilled placeholder — it leaves the gap blank and reports it — so tell
                // the operator which ones were empty rather than letting a silent hole ride into the CEO's brief.
                _host.ShowToast(
                    $"Template “{pick.Template.Name}”: no value for {string.Join(", ", kickoff.MissingPlaceholders)} — left blank.",
                    PluginToastSeverity.Information);
            }

            // The CEO plans on the profile and model configured in the plugin settings (AC-174) — blank = the app
            // default — and gets its briefing as a hidden system prompt given at start (AC-180): who it is, the goal
            // (and the source item), the profiles it can route steps to (with each one's cost), and to emit the plan
            // through the autopilot_plan tool. A hidden prompt at start, not a visible turn, so the operator sees only
            // the plan and their own input — and it cannot race the session's runtime coming up the way a post-start
            // message did.
            var profiles = await _host.GetProfilesAsync();
            var ceoLabel = _settings.CeoProfileLabel();
            // The CEO's identity is the profile it runs under. A blank setting means the app default, which the host
            // resolves to the first configured profile — resolve the same one here so the CEO still knows who it is.
            var ceoIdentity = string.IsNullOrWhiteSpace(ceoLabel) ? profiles.FirstOrDefault()?.Label : ceoLabel;

            var ceo = _context.EmbedSession(new EmbeddedSessionRequest
            {
                ProfileId = ceoLabel,
                Model = _settings.CeoModel(),
                McpServers = PlanningCeoMcpServers(_plan.Plan?.Source is not null),
                WorkingDirectory = AutopilotWorkingDirectory.Resolve(_context, _plan.Plan?.WorkingDirectory),
                AppendSystemPrompt = _plan.Plan is { } plan ? AutopilotCeoBrief.For(plan, profiles, ceoIdentity, _settings.CostStrategy()) : null,
                // The kickoff (AC-189): a chosen template's resolved body, else — free planning — the tracker kickoff for
                // a run triggered from an item, or null for a CEO-first run so it idles waiting for the operator to say
                // what it should achieve. Built above from the picker choice. The host submits it after the runtime is
                // up, so it does not race the session coming online.
                InitialUserMessage = kickoff.Message,
            });
            _ceo = ceo;
            _plan.BindSession(ceo.PaneId);
            await _host.ShowDialogAsync("Plan with the CEO", () => _BuildPlanningContent(ceo), 980, 660);
        }
        catch (Exception)
        {
            // A failed pop-out must not crash the surface; the operator can retry from New run.
        }

        // The dialog closed — approved (the Approve button already submitted the plan to the manager, which starts it or
        // queues it) or cancelled. Either way the planning CEO's job is done, so close it and reset the planning
        // controller so the next New run starts a fresh round. The run itself executes on its own context with its own
        // CEO validator, not this planning session.
        _popoutOpen = false;

        if (_ceo is { } planningCeo)
        {
            _ceo = null;
            _ = planningCeo.CloseAsync();
        }

        if (_plan.Phase == AutopilotPlanPhase.Planning)
        {
            _plan.CancelPlanning();
        }
    }

    // The template picker (AC-189, slice 3): a small modal before the CEO pop-out where the operator picks a template to
    // start the run from, or plans free. It lists the combined templates — the plugin/builtin registrations with any
    // override applied, then the operator's own — each with its origin badge. Returns Cancelled when the operator backed
    // out (the round is then aborted), else the chosen template or null for free planning. When no templates exist at all
    // it skips straight to free planning, so an empty catalogue never puts a needless dialog in the way.
    private async Task<(bool Cancelled, AutopilotTemplate? Template)> _PickTemplateAsync(AutopilotPlanSource? source)
    {
        IReadOnlyList<AutopilotTemplate> templates;
        try
        {
            templates = _templates.List(_host.RegisteredAutopilotTemplates);
        }
        catch (Exception)
        {
            // A template store that cannot list must not block a run — fall back to free planning.
            return (false, null);
        }

        if (templates.Count == 0)
        {
            return (false, null);
        }

        AutopilotTemplate? chosen = null;
        var cancelled = true;

        // The chosen option, held in a plain local the rows set on click and Continue reads — index 0 is "plan free", the
        // templates follow, so it maps to templates[index - 1]. This replaces the ListBox whose SelectedIndex the operator
        // could not move onto item 0 by clicking (default-selected, so re-selecting it was a silent no-op — the live bug):
        // every option is now its own full-width RadioButton with a visible checked state, "Plan free" a big target of its own.
        var selectedIndex = 0;

        var detail = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };

        var radios = new List<RadioButton>();

        void ShowDetail()
        {
            if (selectedIndex <= 0)
            {
                detail.Text = source is { } item
                    ? $"The CEO drafts the plan from {item.Tracker} {item.IssueId} as it does today."
                    : "The CEO asks you what the run should achieve, then plans it with you.";
                return;
            }

            var template = templates[selectedIndex - 1];
            var preview = AutopilotTemplateResolver.Resolve(template.Body, AutopilotTemplateKickoff.SourceData(source));
            var origin = AutopilotTemplateOptionLabel.OriginLabel(template, _PluginName);
            detail.Text = preview.MissingPlaceholders.Count > 0
                ? $"{origin} · placeholders left blank: {string.Join(", ", preview.MissingPlaceholders)}\n\n{preview.Text}"
                : $"{origin}\n\n{preview.Text}";
        }

        // Selecting an option: record it, tint the chosen row so the selection reads at a glance beyond the radio bullet,
        // and refresh the detail pane. Called from each row's checked-change and once up front for the default (plan free).
        void Select(int index)
        {
            selectedIndex = index;
            for (var i = 0; i < radios.Count; i++)
            {
                radios[i].Background = i == index ? _Brush("CockpitSecondaryBgBrush") : Brushes.Transparent;
            }

            ShowDetail();
        }

        // One full-width clickable row per option (AC-189): a RadioButton in a single group, stretched so the whole row is
        // the hit target, with a bold title and a muted subtitle. Unlike the old ListBox row, clicking anywhere on it —
        // including the already-selected one — is an unambiguous, visible act.
        var optionsPanel = new StackPanel { Spacing = 6 };
        var group = Guid.NewGuid().ToString("N");

        RadioButton AddOption(int index, string title, string subtitle)
        {
            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _Brush("CockpitTextPrimaryBrush"),
            });
            if (!string.IsNullOrEmpty(subtitle))
            {
                content.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = _Brush("CockpitTextSecondaryBrush"),
                });
            }

            var radio = new RadioButton
            {
                GroupName = group,
                Content = content,
                Padding = new Thickness(12, 10),
                CornerRadius = new CornerRadius(7),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                IsChecked = index == 0,
            };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    Select(index);
                }
            };
            radios.Add(radio);
            optionsPanel.Children.Add(radio);
            return radio;
        }

        AddOption(0, "Plan free — no template  ·  default", source is { } src
            ? $"Selected by default. Leave it as-is to plan without a template: the CEO drafts the plan from {src.Tracker} {src.IssueId}, exactly as a run without a template does."
            : "Selected by default. Leave it as-is to plan without a template: the CEO asks what the run should achieve, then plans it with you.");
        for (var i = 0; i < templates.Count; i++)
        {
            AddOption(i + 1, AutopilotTemplateOptionLabel.For(templates[i], _PluginName), string.Empty);
        }

        Select(0);

        await _host.ShowDialogAsync("Start a run", () =>
        {
            var start = new Button
            {
                Content = "Continue",
                Padding = new Thickness(15, 8),
                CornerRadius = new CornerRadius(7),
                FontWeight = FontWeight.SemiBold,
                Background = _Brush("CockpitAccentBrush"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x0E)),
                HorizontalAlignment = HorizontalAlignment.Right,
                [DockPanel.DockProperty] = Dock.Right,
            };
            start.Click += (sender, _) =>
            {
                cancelled = false;
                chosen = selectedIndex > 0 ? templates[selectedIndex - 1] : null;
                (sender as Control)?.FindAncestorOfType<Window>()?.Close();
            };

            var cancel = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(13, 8),
                Margin = new Thickness(0, 0, 8, 0),
                CornerRadius = new CornerRadius(7),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = _Brush("CockpitHairlineBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                [DockPanel.DockProperty] = Dock.Right,
            };
            cancel.Click += (sender, _) => (sender as Control)?.FindAncestorOfType<Window>()?.Close();

            var footer = new Border
            {
                Padding = new Thickness(14, 11),
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = _Brush("CockpitHairlineBrush"),
                [DockPanel.DockProperty] = Dock.Bottom,
                Child = new DockPanel { LastChildFill = false, Children = { start, cancel } },
            };

            var body = new StackPanel
            {
                Margin = new Thickness(16, 14),
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Pick a template to start this run from, or plan free.",
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = _Brush("CockpitTextSecondaryBrush"),
                    },
                    optionsPanel,
                    new Border
                    {
                        Padding = new Thickness(10, 8),
                        CornerRadius = new CornerRadius(6),
                        Background = _Brush("CockpitSecondaryBgBrush"),
                        Child = detail,
                    },
                },
            };

            return new DockPanel { LastChildFill = true, Children = { footer, new ScrollViewer { Content = body } } };
        }, 560, 520);

        return (cancelled, chosen);
    }

    // A plugin's readable name for its host-stamped id (a template's OwnerPluginId), or null when no installed plugin
    // matches — the origin-label helper then falls back to the bare id so a Plugin template's origin is never blank.
    private string? _PluginName(string pluginId) =>
        _host.InstalledPlugins.FirstOrDefault(plugin => string.Equals(plugin.Id, pluginId, StringComparison.Ordinal))?.DisplayName;

    // Runs a UI action for the coordinator (session embedding and teardown touch Avalonia controls; the driver loop does
    // not run on the UI thread). Inline when already on it, marshalled otherwise.
    private static async Task _RunOnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }

    private static void _OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private Control _BuildPlanningContent(IEmbeddedSession ceo)
    {
        var planHost = new ContentControl { Content = _BuildBlocks(_plan.Plan) };

        // The run name field (Raymond 2026-07-22): the CEO proposes a name and it pre-fills here, but the operator can
        // override it, and the field must be non-empty before Approve — so a run always carries a recognisable name into
        // the queue and history. A "mirroring" guard tells the CEO's proposal apart from the operator's own typing, so
        // once they edit the name a later CEO re-emit does not overwrite it.
        var nameBox = new TextBox
        {
            FontSize = 12,
            PlaceholderText = "Name this run — or edit the CEO's suggestion",
            [DockPanel.DockProperty] = Dock.Top,
        };
        var nameEdited = false;
        var lastMirrored = string.Empty;
        string Proposed() => _plan.Plan?.SuggestedName ?? string.Empty;
        void SetName(string text)
        {
            lastMirrored = text;
            nameBox.Text = text;
        }

        var (workingDirectoryField, dirBox) = _BuildWorkingDirectoryField();

        // The working directory mirrors the CEO's proposal the same way the name does: the CEO may resolve and propose a
        // folder through the plan tool, which pre-fills here until the operator picks their own — after which a later CEO
        // re-emit does not overwrite it. Falls back to the active session's directory when the CEO proposed none.
        var activeWorkingDirectory = _context.Sessions.ActiveSessionWorkingDirectory ?? string.Empty;
        var dirEdited = false;
        var lastMirroredDir = string.Empty;
        string ProposedDir() => _plan.Plan?.WorkingDirectory is { Length: > 0 } proposed ? proposed : activeWorkingDirectory;
        void SetDir(string text)
        {
            lastMirroredDir = text;
            dirBox.Text = text;
        }

        var approve = _ApproveButton(() => nameBox.Text ?? string.Empty, () => dirBox.Text ?? string.Empty);

        // Approve can start the run only once the CEO has planned at least one step and the run has a name and a working
        // directory — an empty plan has nothing to run, a nameless run cannot be told apart in the queue, and a run
        // needs a folder to work in. Re-checked as the CEO emits or revises the plan and as the operator edits either field.
        void Recheck() => approve.IsEnabled = _HasApprovableSteps()
            && !string.IsNullOrWhiteSpace(nameBox.Text)
            && !string.IsNullOrWhiteSpace(dirBox.Text);
        dirBox.TextChanged += (_, _) =>
        {
            // Same mirroring guard as the name: a value that is not what we last mirrored is the operator picking their
            // own folder, from then on their choice stands and a later CEO re-emit does not overwrite it.
            if ((dirBox.Text ?? string.Empty) != lastMirroredDir)
            {
                dirEdited = true;
            }

            Recheck();
        };
        nameBox.TextChanged += (_, _) =>
        {
            // A change whose text is not the one we just mirrored in is the operator typing — from then on their name
            // stands and a later CEO re-emit does not overwrite it. Comparing the value (not a timing flag) is robust
            // whether Avalonia raises TextChanged synchronously or deferred: setting Text from null to "" at build used
            // to trip a mirroring flag and wrongly mark the field edited, which then blocked every auto-fill.
            if ((nameBox.Text ?? string.Empty) != lastMirrored)
            {
                nameEdited = true;
            }

            Recheck();
        };

        SetName(Proposed());
        SetDir(ProposedDir());
        Recheck();
        void OnPlanChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
        {
            planHost.Content = _BuildBlocks(_plan.Plan);
            if (!nameEdited)
            {
                SetName(Proposed());
            }

            if (!dirEdited)
            {
                SetDir(ProposedDir());
            }

            Recheck();
        });
        _plan.Changed += OnPlanChanged;
        planHost.DetachedFromVisualTree += (_, _) => _plan.Changed -= OnPlanChanged;

        // A one-line hint above the plan so it is clear the plan is shaped through the conversation, not by clicking the
        // blocks: steps and their models are added, removed or re-targeted by asking the CEO in the chat (AC-174 — the
        // clickable per-step model pills were dropped in favour of discussing the split with the CEO).
        var hint = new TextBlock
        {
            Text = "Add, remove or re-target steps — including which model runs each — by asking the CEO in the chat.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(14, 12, 14, 6),
            Foreground = _Brush("CockpitTextFaintBrush"),
            [DockPanel.DockProperty] = Dock.Top,
        };

        var left = new Border
        {
            Width = 372,
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Background = _Brush("CockpitSecondaryBgBrush"),
            [DockPanel.DockProperty] = Dock.Left,
            Child = new DockPanel
            {
                LastChildFill = true,
                Children = { hint, new ScrollViewer { Content = planHost } },
            },
        };

        var footer = new Border
        {
            Padding = new Thickness(14, 11),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            [DockPanel.DockProperty] = Dock.Bottom,
            Child = new StackPanel
            {
                Spacing = 9,
                Children =
                {
                    workingDirectoryField,
                    new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "Run name",
                                FontSize = 10.5,
                                FontWeight = FontWeight.SemiBold,
                                Foreground = _Brush("CockpitTextSecondaryBrush"),
                            },
                            nameBox,
                        },
                    },
                    new DockPanel
                    {
                        LastChildFill = false,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "Iterate with the CEO — approval is the single gate, then it runs autonomously.",
                                FontSize = 11,
                                TextWrapping = TextWrapping.Wrap,
                                VerticalAlignment = VerticalAlignment.Center,
                                Foreground = _Brush("CockpitTextFaintBrush"),
                                [DockPanel.DockProperty] = Dock.Left,
                            },
                            approve,
                            _CancelButton(),
                        },
                    },
                },
            },
        };

        // A CEO-only "working" cue over the session view (AC-195): the CEO's planning turn can run silently for minutes,
        // and the shared session view's own indicator stays deaf during streaming on purpose — so without this the
        // pop-out reads as hung. It follows the embedded session's busy signal alone, leaving the global indicator
        // untouched. Overlaid at the TOP-CENTRE of the chat pane (AC-214): the old top-right corner placement sat right
        // next to the session's reading-level dropdown and read as an unnoticeable afterthought; centred over the chat,
        // where the CEO is actually working, it is the clear "the CEO is thinking" banner the operator looks for, while
        // still clearing the composer and the transcript's live text below it.
        var working = _BuildCeoWorkingCue();
        var busy = new CeoBusyIndicatorModel(ceo, isWorking =>
            Dispatcher.UIThread.Post(() => working.IsVisible = isWorking));
        var right = new Border
        {
            Child = new Grid { Children = { ceo.View, working } },
        };
        right.DetachedFromVisualTree += (_, _) => busy.Dispose();

        return new DockPanel { LastChildFill = true, Children = { footer, left, right } };
    }

    // The CEO-only "working" cue (AC-195/AC-214): a pill that appears while the CEO's planning turn is in flight, so a
    // long silent turn shows progress rather than looking stuck. Hidden until the busy signal lights it. Anchored
    // top-centre over the chat pane (AC-214) — not tucked in the top-right corner beside the reading-level dropdown where
    // it went unnoticed — and given an accent-tinted fill so it reads as a clear banner over the conversation.
    private Control _BuildCeoWorkingCue() => new Border
    {
        IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 12, 0, 0),
        Background = _Brush("CockpitAccentSoftBrush") ?? _Brush("CockpitPanelBgBrush"),
        BorderThickness = new Thickness(1),
        BorderBrush = _Brush("CockpitStatusBusyBrush") ?? _Brush("CockpitHairlineBrush"),
        CornerRadius = new CornerRadius(13),
        Padding = new Thickness(14, 6),
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new Border
                {
                    Width = 9,
                    Height = 9,
                    CornerRadius = new CornerRadius(4.5),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = _Brush("CockpitStatusBusyBrush"),
                },
                new TextBlock
                {
                    Text = "CEO is working…",
                    FontSize = 12.5,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = _Brush("CockpitTextPrimaryBrush") ?? _Brush("CockpitTextSecondaryBrush"),
                },
            },
        },
    };

    // The plan is approvable only when the CEO has planned at least one step — an empty plan would start a run with
    // nothing to do.
    private bool _HasApprovableSteps() => _plan.Plan is { Steps.Count: > 0 };

    // The working-directory field (AC-174): a run planned from a tracker issue has no session, so the operator names the
    // folder it works in here — the same folders the New-session dialog offers (pinned favorites and recents, loaded on
    // demand when the history button is clicked) plus a Browse. A non-git folder is allowed; the hint says the run then
    // works in it without per-step isolation. Returns the field and its text box so the caller wires the mirroring and
    // the approval gate; the box's value is set by the caller (the CEO's proposal or the active session's directory).
    private (Control Field, TextBox Box) _BuildWorkingDirectoryField()
    {
        var box = new TextBox
        {
            FontSize = 12,
            // Match the Compact buttons beside it so the three controls in the row are the same height (AC-200).
            MinHeight = 28,
            PlaceholderText = "Folder this run works in — pick a recent one or browse",
        };

        // The history and Browse buttons follow the app-wide Compact button pattern (Theme.axaml: Padding 10,0 +
        // MinHeight 28 + centred content), the same as the New-session / profile dialogs — so they align with the text
        // box in the row instead of stretching to their own height (AC-200).
        var pick = new Button
        {
            Content = new MaterialIcon { Kind = MaterialIconKind.History, Width = 16, Height = 16 },
            Classes = { "Compact" },
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            [ToolTip.TipProperty] = "Recent and pinned folders",
            [DockPanel.DockProperty] = Dock.Right,
        };
        pick.Click += async (_, _) =>
        {
            try
            {
                // Loaded on click, not at build, so the flyout is always current and there is no async race at build time.
                var remembered = await _host.GetRememberedWorkingPathsAsync();
                var menu = new MenuFlyout();
                foreach (var favorite in remembered.Favorites)
                {
                    menu.Items.Add(_RememberedPathItem($"★  {favorite}", favorite, box));
                }

                // Recents that are not already pinned above, so a folder that is both a favorite and recent shows once
                // (the shared history keeps the two lists independent — the New-session quick-pick dedupes the same way).
                foreach (var recent in remembered.Recents.Where(recent => !remembered.Favorites.Any(favorite => _SamePath(favorite, recent))))
                {
                    menu.Items.Add(_RememberedPathItem(recent, recent, box));
                }

                if (menu.Items.Count == 0)
                {
                    menu.Items.Add(new MenuItem { Header = "No remembered folders yet", IsEnabled = false });
                }

                menu.ShowAt(pick);
            }
            catch (Exception)
            {
                // Loading the remembered folders must not crash the dialog (an async-void handler's throw would reach
                // the dispatcher) — the operator can still type or browse to a folder.
            }
        };

        var browse = new Button
        {
            Content = "Browse…",
            Classes = { "Compact" },
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            [DockPanel.DockProperty] = Dock.Right,
        };
        browse.Click += async (_, _) =>
        {
            try
            {
                // The picker needs the dialog window's TopLevel — taken from the button, which is in that window's tree.
                if (TopLevel.GetTopLevel(browse)?.StorageProvider is not { } storage)
                {
                    return;
                }

                var start = string.IsNullOrWhiteSpace(box.Text) ? null : await storage.TryGetFolderFromPathAsync(box.Text);
                var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Choose the folder this run works in",
                    AllowMultiple = false,
                    SuggestedStartLocation = start,
                });

                if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
                {
                    box.Text = path;
                }
            }
            catch (Exception)
            {
                // A cancelled or failed folder pick must not crash the dialog; the field is left as it was.
            }
        };

        var field = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "Working directory",
                    FontSize = 10.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = _Brush("CockpitTextSecondaryBrush"),
                },
                new DockPanel { LastChildFill = true, Children = { browse, pick, box } },
                new TextBlock
                {
                    Text = "If this folder isn't a git repository, the run works in it directly — steps run without isolation.",
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = _Brush("CockpitTextFaintBrush"),
                },
            },
        };

        return (field, box);
    }

    private static MenuItem _RememberedPathItem(string header, string path, TextBox box)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => box.Text = path;
        return item;
    }

    // Two paths are the same folder when they match case-insensitively ignoring a trailing separator — mirrors the
    // shared WorkingPathHistory comparison, so the favorite/recent dedupe here matches how the history itself dedupes.
    private static bool _SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    private Button _ApproveButton(Func<string> nameProvider, Func<string> workingDirectoryProvider)
    {
        var button = new Button
        {
            Content = "Approve plan & start",
            Padding = new Thickness(15, 8),
            CornerRadius = new CornerRadius(7),
            FontWeight = FontWeight.SemiBold,
            Background = _Brush("CockpitAccentBrush"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x0E)),
            HorizontalAlignment = HorizontalAlignment.Right,
            [DockPanel.DockProperty] = Dock.Right,
        };
        button.Click += (sender, _) =>
        {
            // Approve submits the planned draft — carrying the operator's (or the CEO's) run name and their chosen
            // working directory — to the run manager, which runs it now if there is a free slot or queues it behind the
            // others. It does not run on the planning controller. The button is only enabled once the plan has steps, a
            // name and a directory, so all three hold here.
            if (_plan.Plan is { Steps.Count: > 0 } plan)
            {
                var name = nameProvider().Trim();
                var approved = string.IsNullOrEmpty(name) ? plan : plan.WithName(name);
                _manager.Submit(approved.WithWorkingDirectory(workingDirectoryProvider().Trim()));
            }

            (sender as Control)?.FindAncestorOfType<Window>()?.Close();
        };
        return button;
    }

    private Button _CancelButton()
    {
        var button = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(13, 8),
            Margin = new Thickness(0, 0, 8, 0),
            CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            [DockPanel.DockProperty] = Dock.Right,
        };
        button.Click += (sender, _) => (sender as Control)?.FindAncestorOfType<Window>()?.Close();
        return button;
    }

    // The step blocks as a column — shared by the run pipeline (left) and the planning pop-out (left). The CEO always
    // heads the column: it is part of the whole, planning the work and validating every step, so it reads as the
    // orchestrator above the real steps.
    private Control _BuildBlocks(AutopilotPlan? plan)
    {
        var blocks = new StackPanel { Spacing = 0 };
        blocks.Children.Add(_BuildCeoBlock());
        if (plan is not null)
        {
            var index = 1;
            foreach (var step in plan.Steps)
            {
                blocks.Children.Add(_BuildBlock(index++, step));
            }
        }

        return blocks;
    }

    // The fixed CEO block at the top of the pipeline (Raymond 2026-07-21): the orchestrator, active throughout — it
    // plans in the planning phase and validates each step in the run — so it carries the busy dot. Its chip shows the
    // configured CEO profile (and model, when set).
    private Control _BuildCeoBlock()
    {
        var dot = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 11, 0),
            [DockPanel.DockProperty] = Dock.Left,
            Background = _Brush("CockpitStatusBusyBrush"),
            Child = new MaterialIcon
            {
                Kind = MaterialIconKind.RobotHappyOutline,
                Width = 11,
                Height = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x1A, 0x13)),
            },
        };

        var meta = new StackPanel { Spacing = 3 };
        meta.Children.Add(new TextBlock { Text = "CEO", FontWeight = FontWeight.SemiBold, Foreground = _Brush("CockpitTextPrimaryBrush") });
        meta.Children.Add(new TextBlock
        {
            Text = "Plans the work, then validates each step against its acceptance.",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        });
        if (_CeoChip() is { } chip)
        {
            meta.Children.Add(chip);
        }

        return new Border
        {
            Padding = new Thickness(14, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Child = new DockPanel { LastChildFill = true, Children = { dot, meta } },
        };
    }

    private Control? _CeoChip()
    {
        var profile = _settings.CeoProfileLabel();
        if (string.IsNullOrWhiteSpace(profile))
        {
            return null;
        }

        var model = _settings.CeoModel();
        var label = string.IsNullOrWhiteSpace(model) ? profile : $"{profile} · {model}";
        return new Border
        {
            Background = _Brush("CockpitPanelBgBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = label, FontSize = 10.5, Foreground = _Brush("CockpitTextSecondaryBrush") },
        };
    }

    // One running run's pipeline: its goal and step blocks on the left, its active step's session (or a hint) on the right.
    private Control _BuildPipeline(AutopilotRunContext context)
    {
        var controller = context.Controller;
        var plan = controller.Plan;

        var goalText = new TextBlock
        {
            Text = plan?.Label is { Length: > 0 } text ? text : "Autopilot run",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // While the run is live the operator can stop it (AC-196): a mid-run stop settles it Stopped and records it in
        // history, rather than the run vanishing silently while still Running. Only shown while the run is actually
        // running or waiting — a settled run has nothing left to stop.
        // TODO(AC-196): a confirmation ("Stop run? unmerged work is discarded") is UX-wanted but out of scope here.
        var live = controller.Phase is AutopilotPlanPhase.Running or AutopilotPlanPhase.AwaitingOperator;
        var stop = new Button
        {
            Content = "Stop run",
            Classes = { "Compact" },
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible = live,
            Margin = new Thickness(8, 0, 0, 0),
            [DockPanel.DockProperty] = Dock.Right,
        };
        stop.Click += (_, _) =>
        {
            // Set the Stopped phase first, then cancel (order matters — see AutopilotPlanController.Stop): the driver
            // settles only when every step finished, never on a mid-run cancel, so the phase the surface snapshots after
            // the run tears down stays Stopped rather than being overwritten.
            context.Controller.Stop("Stopped by operator");
            context.Cancel();
        };

        var goal = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(14, 12, 14, 8),
            [DockPanel.DockProperty] = Dock.Top,
            Children = { stop, goalText },
        };

        var left = new Border
        {
            Width = 300,
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Background = _Brush("CockpitSecondaryBgBrush"),
            [DockPanel.DockProperty] = Dock.Left,
            Child = new DockPanel { LastChildFill = true, Children = { goal, new ScrollViewer { Content = _BuildBlocks(plan) } } },
        };

        // The right pane, in priority: a blockade the operator must answer (AC-155); the CEO's validation of a finished
        // step, shown as the CEO session under a clear banner so it is obvious the CEO is reviewing (Raymond 2026-07-22);
        // the live step session under an intervene bar; or a hint between steps.
        var validating = context.IsValidating && context.CeoView is not null;
        var right = new Border
        {
            Padding = controller.Phase == AutopilotPlanPhase.AwaitingOperator || (!validating && context.StepView is null) ? new Thickness(16) : new Thickness(0),
            Child = controller.Phase == AutopilotPlanPhase.AwaitingOperator
                ? _BuildBlockadePanel(context)
                : context.IsValidating && context.CeoView is { } ceoView
                    ? _BuildValidatingSurface(ceoView)
                    : context.StepView is { } stepView
                        ? _BuildStepSurface(context, stepView)
                        : controller.ActiveStep is { } active
                            ? _CentredHint(MaterialIconKind.PlayCircleOutline, active.Title, active.Description)
                            : _CentredHint(MaterialIconKind.RobotOutline, "Waiting for the next step", "The running step's live session shows here."),
        };

        return new DockPanel { LastChildFill = true, Children = { left, right } };
    }

    // The running step's session under an intervene bar (AC-174): the step runs autonomously with its composer off, so a
    // bar over it says so and offers one button that hands the operator the keyboard (EnableCurrentStepInput). Kept a
    // thin affordance — the operator stays out of the loop unless they choose to step in.
    private Control _BuildStepSurface(AutopilotRunContext context, Control stepView)
    {
        // The step view is a persistent control (the live embedded session), reused across renders. _Render rebuilds
        // this whole pipeline while the previous one is still on the host, so the view still sits in the previous
        // render's container — and Avalonia throws when a control that still has a parent is placed into a new one.
        // Detach it from that container first.
        _DetachFromParent(stepView);

        var intervene = new Button
        {
            Content = "Enable input to intervene",
            Padding = new Thickness(11, 5),
            CornerRadius = new CornerRadius(6),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            [DockPanel.DockProperty] = Dock.Right,
        };
        intervene.Click += (_, _) => context.Coordinator.EnableCurrentStepInput();

        var bar = new Border
        {
            Padding = new Thickness(12, 7),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Background = _Brush("CockpitSecondaryBgBrush"),
            [DockPanel.DockProperty] = Dock.Top,
            Child = new DockPanel
            {
                LastChildFill = false,
                Children =
                {
                    intervene,
                    new TextBlock
                    {
                        Text = "This step runs autonomously — its input is off.",
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = _Brush("CockpitTextFaintBrush"),
                        [DockPanel.DockProperty] = Dock.Left,
                    },
                },
            },
        };

        // Host the reused step view in a fresh single-child Border rather than adding it straight to the DockPanel's
        // Children: a Panel's Children collection rejects a control that still has a parent from the previous render (the
        // step view is persistent and reparented each render), and throws mid-render — a Border's single-child slot
        // reparents it cleanly, the way the right pane held it before this bar was added.
        return new DockPanel
        {
            LastChildFill = true,
            Children = { bar, new Border { Child = stepView } },
        };
    }

    // The CEO's validation of a finished step (Raymond 2026-07-22): the CEO session under a prominent accent banner, so it
    // reads clearly that the CEO is now reviewing the work against its acceptance — not the finished worker still sitting
    // in the pane. The CEO view is a persistent control, reparented each render, so it is detached from its old container
    // first, the same way the step view is.
    private Control _BuildValidatingSurface(Control ceoView)
    {
        _DetachFromParent(ceoView);

        var bar = new Border
        {
            Padding = new Thickness(12, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Background = _Brush("CockpitStatusBusyBrush"),
            [DockPanel.DockProperty] = Dock.Top,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                Children =
                {
                    new MaterialIcon
                    {
                        Kind = MaterialIconKind.ClipboardCheckOutline,
                        Width = 16,
                        Height = 16,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x1A, 0x13)),
                    },
                    new TextBlock
                    {
                        Text = "The CEO is validating this step against its acceptance…",
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x1A, 0x13)),
                    },
                },
            },
        };

        return new DockPanel
        {
            LastChildFill = true,
            Children = { bar, new Border { Child = ceoView } },
        };
    }

    // Removes a control from whatever container currently parents it, so a persistent control (the live step view) can be
    // re-placed into a freshly built tree without Avalonia rejecting it for still having a parent. A no-op the first time,
    // when it has none.
    private static void _DetachFromParent(Control control)
    {
        switch (control.Parent)
        {
            case Decorator decorator when ReferenceEquals(decorator.Child, control):
                decorator.Child = null;
                break;
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case ContentControl content when ReferenceEquals(content.Content, control):
                content.Content = null;
                break;
        }
    }

    // The blockade panel (AC-155): the step's question to the operator, an answer box, and a Send that relays the reply
    // to the blocked session and resumes the run. The step blocks stay on the left; only the right pane changes.
    private Control _BuildBlockadePanel(AutopilotRunContext context)
    {
        var answer = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 96,
            PlaceholderText = "Your answer for the run…",
        };

        var send = new Button
        {
            Content = "Send answer & resume",
            Padding = new Thickness(15, 8),
            CornerRadius = new CornerRadius(7),
            FontWeight = FontWeight.SemiBold,
            Background = _Brush("CockpitAccentBrush"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x0E)),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        send.Click += (_, _) =>
        {
            var text = answer.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                _ = context.Coordinator.AnswerBlockadeAsync(text);
            }
        };

        return new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12,
            MaxWidth = 520,
            Children =
            {
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = "Waiting for you", FontWeight = FontWeight.SemiBold, Foreground = _Brush("CockpitStatusWaitingBrush") },
                        new TextBlock
                        {
                            Text = context.Controller.PendingQuestion ?? "The run is blocked and needs your answer.",
                            FontSize = 14,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = _Brush("CockpitTextPrimaryBrush"),
                        },
                    },
                },
                answer,
                send,
            },
        };
    }

    private Control _BuildBlock(int index, AutopilotStep step)
    {
        var dot = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 11, 0),
            [DockPanel.DockProperty] = Dock.Left,
            Background = step.Status switch
            {
                AutopilotStepStatus.Passed => _Brush("CockpitStatusDoneBrush"),
                AutopilotStepStatus.Running => _Brush("CockpitStatusBusyBrush"),
                AutopilotStepStatus.Failed => _Brush("CockpitStatusErrorBrush"),
                AutopilotStepStatus.Blocked => _Brush("CockpitStatusWaitingBrush"),
                _ => _Brush("CockpitSecondaryBgBrush"),
            },
            BorderThickness = new Thickness(step.Status == AutopilotStepStatus.Pending ? 2 : 0),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Child = new TextBlock
            {
                Text = step.Status == AutopilotStepStatus.Passed ? "✓" : $"{index}",
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = step.Status is AutopilotStepStatus.Pending
                    ? _Brush("CockpitTextFaintBrush")
                    : new SolidColorBrush(Color.FromRgb(0x0F, 0x1A, 0x13)),
            },
        };

        var meta = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock
                {
                    Text = step.Mode == GateMode.Hard ? $"{step.Title}  ·  required" : step.Title,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = step.Status == AutopilotStepStatus.Pending ? _Brush("CockpitTextFaintBrush") : _Brush("CockpitTextPrimaryBrush"),
                },
                new TextBlock { Text = step.Description, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = _Brush("CockpitTextSecondaryBrush") },
                _ModelChip(step),
            },
        };

        // A status line so the operator sees where the step is without decoding the dot colour (Raymond) — and, when the
        // run has something to say (why a step failed, or that it was refused), the note under it, so a failed step is
        // never a silent red dot.
        if (_StepStatusText(step) is { Length: > 0 } statusText)
        {
            meta.Children.Add(new TextBlock
            {
                Text = statusText,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = _StepStatusBrush(step.Status),
            });
        }

        if (!string.IsNullOrWhiteSpace(step.Note))
        {
            meta.Children.Add(new TextBlock
            {
                Text = step.Note,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = step.Status == AutopilotStepStatus.Failed ? _Brush("CockpitStatusErrorBrush") : _Brush("CockpitTextSecondaryBrush"),
            });
        }

        return new Border
        {
            Padding = new Thickness(14, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Child = new DockPanel { LastChildFill = true, Children = { dot, meta } },
        };
    }

    // A short status word for a step, so the operator reads its state as words rather than a dot colour. A running step
    // on its second-or-later attempt reads as a rework with the attempt number (Raymond 2026-07-22), so a step that is
    // re-run after the CEO turned it down does not look like it simply never finished. Empty for a pending step.
    private static string _StepStatusText(AutopilotStep step) => step.Status switch
    {
        AutopilotStepStatus.Running => step.Attempts > 1 ? $"Reworking — attempt {step.Attempts}…" : "Running…",
        AutopilotStepStatus.Passed => "Passed",
        AutopilotStepStatus.Failed => "Failed",
        AutopilotStepStatus.Blocked => "Waiting for you",
        AutopilotStepStatus.Skipped => "Skipped",
        _ => string.Empty,
    };

    private IBrush? _StepStatusBrush(AutopilotStepStatus status) => status switch
    {
        AutopilotStepStatus.Passed => _Brush("CockpitStatusDoneBrush"),
        AutopilotStepStatus.Running => _Brush("CockpitStatusBusyBrush"),
        AutopilotStepStatus.Failed => _Brush("CockpitStatusErrorBrush"),
        AutopilotStepStatus.Blocked => _Brush("CockpitStatusWaitingBrush"),
        _ => _Brush("CockpitTextSecondaryBrush"),
    };

    private Border _ModelChip(AutopilotStep step)
    {
        var label = string.IsNullOrWhiteSpace(step.Model) ? step.ProfileLabel : $"{step.ProfileLabel} · {step.Model}";
        if (step.AgentCount > 1)
        {
            label += $"  ×{step.AgentCount}";
        }

        return new Border
        {
            Background = _Brush("CockpitPanelBgBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = label, FontSize = 10.5, Foreground = _Brush("CockpitTextSecondaryBrush") },
        };
    }

    private Control _CentredHint(MaterialIconKind icon, string title, string subtitle) =>
        new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
            MaxWidth = 420,
            Children =
            {
                new MaterialIcon { Kind = icon, Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Center, Foreground = _Brush("CockpitTextFaintBrush") },
                new TextBlock { Text = title, HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = subtitle,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = _Brush("CockpitTextSecondaryBrush"),
                },
            },
        };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
