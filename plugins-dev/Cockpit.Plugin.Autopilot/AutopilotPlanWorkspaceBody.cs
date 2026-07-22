using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
    private readonly List<AutopilotRunContext> _activeContexts = [];
    private readonly ContentControl _bodyHost = new();
    private bool _popoutOpen;
    private int _completedRuns;
    private IEmbeddedSession? _ceo;

    public AutopilotPlanWorkspaceBody(ICockpitHost host, IWorkspaceContext context, AutopilotSettings settings, AutopilotPlanController plan, AutopilotRunManager manager, AutopilotRunQueue queue, AutopilotRunHistory history)
    {
        _host = host;
        _context = context;
        _settings = settings;
        _plan = plan;
        _manager = manager;
        _queue = queue;
        _history = history;

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

    // One settled run in history: an outcome dot, its name and when it finished, and a one-line step summary (each step's
    // title with a ✓/✗ so it reads at a glance what was done). A blocked run also shows why, so history explains a stop.
    private Control _BuildHistoryRow(AutopilotRunRecord record)
    {
        var mergeReady = record.Outcome == AutopilotPlanPhase.MergeReady;
        var dot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 10, 0),
            [DockPanel.DockProperty] = Dock.Left,
            Background = mergeReady ? _Brush("CockpitStatusDoneBrush") : _Brush("CockpitStatusWaitingBrush"),
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

        var meta = new StackPanel { Spacing = 2, Children = { title } };
        meta.Children.Add(new TextBlock
        {
            Text = mergeReady ? "Merge-ready" : $"Blocked — {record.BlockReason}",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = mergeReady ? _Brush("CockpitStatusDoneBrush") : _Brush("CockpitStatusWaitingBrush"),
        });

        if (_HistoryStepsSummary(record.Steps) is { Length: > 0 } steps)
        {
            meta.Children.Add(new TextBlock
            {
                Text = steps,
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _Brush("CockpitTextSecondaryBrush"),
            });
        }

        return new Border
        {
            Padding = new Thickness(12, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Child = new DockPanel { LastChildFill = true, Children = { dot, meta } },
        };
    }

    // A compact one-line "what was done" for a settled run: each step's title prefixed with a mark for how it ended.
    private static string _HistoryStepsSummary(IReadOnlyList<AutopilotRunStepRecord> steps)
    {
        if (steps.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("   ", steps.Select(step =>
        {
            var mark = step.Status switch
            {
                AutopilotStepStatus.Passed => "✓",
                AutopilotStepStatus.Failed => "✗",
                AutopilotStepStatus.Skipped => "–",
                AutopilotStepStatus.Blocked => "⏸",
                _ => "·",
            };
            return $"{mark} {step.Title}";
        }));
    }

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

        var head = new Border
        {
            Padding = new Thickness(12, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            [DockPanel.DockProperty] = Dock.Top,
            Child = new DockPanel { LastChildFill = false, Children = { newRun, summary } },
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
        if (_popoutOpen)
        {
            return;
        }

        _plan.BeginPlanning(AutopilotPlan.Empty(source: null, goal: string.Empty));
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
    private void _RecordAndNotify(AutopilotPlan? plan, AutopilotPlanPhase outcome, string? blockReason)
    {
        var settled = outcome is AutopilotPlanPhase.MergeReady or AutopilotPlanPhase.Blocked;
        if (settled && plan is not null)
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
            if (outcome == AutopilotPlanPhase.MergeReady)
            {
                _host.ShowToast($"Run “{label}” is merge-ready.", PluginToastSeverity.Success);
            }
            else
            {
                _host.ShowToast($"Run “{label}” is blocked — {blockReason}", PluginToastSeverity.Warning);
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

            _ceo = _context.EmbedSession(new EmbeddedSessionRequest
            {
                ProfileId = ceoLabel,
                Model = _settings.CeoModel(),
                WorkingDirectory = AutopilotWorkingDirectory.Resolve(_context),
                AppendSystemPrompt = _plan.Plan is { } plan ? AutopilotCeoBrief.For(plan, profiles, ceoIdentity, _settings.CostStrategy()) : null,
            });
            _plan.BindSession(_ceo.PaneId);
            await _host.ShowDialogAsync("Plan with the CEO", () => _BuildPlanningContent(_ceo!), 980, 660);
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
        var mirroring = false;
        string Proposed() => _plan.Plan is { } p ? (string.IsNullOrWhiteSpace(p.Name) ? p.Goal : p.Name) : string.Empty;
        void SetName(string text)
        {
            mirroring = true;
            nameBox.Text = text;
            mirroring = false;
        }

        var approve = _ApproveButton(() => nameBox.Text ?? string.Empty);

        // Approve can start the run only once the CEO has planned at least one step and the run has a name — an empty
        // plan has nothing to run, and a nameless run cannot be told apart in the queue. Re-checked as the CEO emits or
        // revises the plan and as the operator types the name.
        void Recheck() => approve.IsEnabled = _HasApprovableSteps() && !string.IsNullOrWhiteSpace(nameBox.Text);
        nameBox.TextChanged += (_, _) =>
        {
            if (!mirroring)
            {
                nameEdited = true;
            }

            Recheck();
        };

        SetName(Proposed());
        Recheck();
        void OnPlanChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
        {
            planHost.Content = _BuildBlocks(_plan.Plan);
            if (!nameEdited)
            {
                SetName(Proposed());
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

        var right = new Border { Child = ceo.View };
        return new DockPanel { LastChildFill = true, Children = { footer, left, right } };
    }

    // The plan is approvable only when the CEO has planned at least one step — an empty plan would start a run with
    // nothing to do.
    private bool _HasApprovableSteps() => _plan.Plan is { Steps.Count: > 0 };

    private Button _ApproveButton(Func<string> nameProvider)
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
            // Approve submits the planned draft — carrying the operator's (or the CEO's) run name — to the run manager,
            // which runs it now if there is a free slot or queues it behind the others. It does not run on the planning
            // controller. The button is only enabled once the plan has steps and a name, so both hold here.
            if (_plan.Plan is { Steps.Count: > 0 } plan)
            {
                var name = nameProvider().Trim();
                _manager.Submit(string.IsNullOrEmpty(name) ? plan : plan.WithName(name));
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

        var goal = new TextBlock
        {
            Text = plan?.Label is { Length: > 0 } text ? text : "Autopilot run",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 12, 14, 8),
            [DockPanel.DockProperty] = Dock.Top,
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

        // Awaiting the operator (AC-155): the run parked on a blockade — show the question and an answer box instead of
        // the running session. Otherwise the live step session (under an intervene bar), or a hint between steps.
        var right = new Border
        {
            Padding = controller.Phase == AutopilotPlanPhase.AwaitingOperator || context.StepView is null ? new Thickness(16) : new Thickness(0),
            Child = controller.Phase == AutopilotPlanPhase.AwaitingOperator
                ? _BuildBlockadePanel(context)
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
