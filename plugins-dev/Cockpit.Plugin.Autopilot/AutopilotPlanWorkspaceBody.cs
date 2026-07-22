using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions;
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
    private readonly AutopilotRunCoordinator _coordinator;
    private readonly ContentControl _bodyHost = new();
    private bool _popoutOpen;
    private IEmbeddedSession? _ceo;
    private Control? _stepView;
    private CancellationTokenSource? _runCts;
    private bool _runStarted;

    public AutopilotPlanWorkspaceBody(ICockpitHost host, IWorkspaceContext context, AutopilotSettings settings, AutopilotPlanController plan, AutopilotRunCoordinator coordinator)
    {
        _host = host;
        _context = context;
        _settings = settings;
        _plan = plan;
        _coordinator = coordinator;

        // Cancel a running autonomous run when this workspace is really closed (its tab dismissed, not a mere tab-switch)
        // so it does not keep going headless with no surface to stop it (AC-174). Subscribed for the body's whole life —
        // the context and the body are dropped together at close, so there is nothing to unsubscribe.
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

    // The workspace was really closed (WorkspacesViewModel raised it on tab-dismiss, on the UI thread): stop the run so it
    // does not run on headless. Cancelling is enough — the driver loop and the coordinator's awaits unwind, closing the
    // step sessions and the CEO in the run's finally.
    private void _OnWorkspaceClosed(object? sender, EventArgs e) => _runCts?.Cancel();

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

        _bodyHost.Content = _plan.Phase == AutopilotPlanPhase.Planning
            ? _CentredHint(MaterialIconKind.RobotHappyOutline, "Planning with the CEO…", "Shape the plan in the pop-out, then approve it to start the run.")
            : _plan.Plan is { Steps.Count: > 0 } plan
                ? _BuildPipeline(plan)
                : _BuildEmptyState();
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

            // Approved (the plan froze to Running): the CEO stays alive as the run's validator and the autonomous run
            // starts over this same surface. Anything else (cancelled, or an error above): close the CEO — nothing runs.
            if (_plan.Phase == AutopilotPlanPhase.Running && _ceo is { } approved)
            {
                _StartRun(approved);
                return;
            }
        }
        catch (Exception)
        {
            // A failed pop-out must not crash the surface; the operator can retry from the trigger.
        }

        // Dismissed or errored (not approved — the approved path returned above): let the pop-out reopen for a fresh
        // round, tear the CEO down, and clear the draft so the surface returns to its empty state instead of this render
        // immediately reopening the pop-out on the still-Planning phase.
        _popoutOpen = false;

        if (_ceo is { } cancelled)
        {
            _ceo = null;
            _ = cancelled.CloseAsync();
        }

        if (_plan.Phase == AutopilotPlanPhase.Planning)
        {
            _plan.CancelPlanning();
        }
    }

    // Start the autonomous run once, on approval: the driver runs each step's agent and the still-live CEO validates
    // each against its acceptance. Fire-and-forget so the dialog-close returns; the surface follows through plan.Changed
    // and the step-session callback. The CEO is torn down when the run settles.
    private void _StartRun(IEmbeddedSession ceo)
    {
        if (_runStarted)
        {
            return;
        }

        _runStarted = true;
        _runCts = new CancellationTokenSource();
        _ = _RunAsync(ceo, _runCts.Token);
    }

    private async Task _RunAsync(IEmbeddedSession ceo, CancellationToken cancellationToken)
    {
        try
        {
            await _coordinator.RunAsync(_context, ceo, _settings, _ShowStepSession, _RunOnUiAsync, cancellationToken);
        }
        catch (Exception)
        {
            // A failed or cancelled run must not crash the surface; the pipeline shows the settled or blocked phase.
        }
        finally
        {
            // All on the UI thread: clearing the step view and rendering touch Avalonia controls, and marshalling the CEO
            // teardown + the cts dispose here too keeps them ordered against _OnWorkspaceClosed's cancel (also UI-thread),
            // so a close racing a settle cannot dispose the cts out from under the cancel.
            _OnUi(() =>
            {
                _stepView = null;

                if (_ceo is { } settled)
                {
                    _ceo = null;
                    _ = settled.CloseAsync();
                }

                _runCts?.Dispose();
                _runCts = null;
                _Render();
            });
        }
    }

    // The coordinator hands the running step's live view here — show it in the run pipeline's right pane. Called inside
    // the coordinator's UI marshalling, so it is already on the UI thread.
    private void _ShowStepSession(Control view)
    {
        _stepView = view;
        _Render();
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
        var approve = _ApproveButton();

        // Approve can start the run only once the CEO has actually planned steps — an empty plan has nothing to run, so
        // it stays disabled until there is at least one step, and re-checks as the CEO emits or revises the plan.
        approve.IsEnabled = _HasApprovableSteps();
        void OnPlanChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
        {
            planHost.Content = _BuildBlocks(_plan.Plan);
            approve.IsEnabled = _HasApprovableSteps();
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
            Child = new DockPanel
            {
                LastChildFill = false,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Iterate with the CEO — approval is the single gate, then it runs autonomously.",
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = _Brush("CockpitTextFaintBrush"),
                        [DockPanel.DockProperty] = Dock.Left,
                    },
                    approve,
                    _CancelButton(),
                },
            },
        };

        var right = new Border { Child = ceo.View };
        return new DockPanel { LastChildFill = true, Children = { footer, left, right } };
    }

    // The plan is approvable only when the CEO has planned at least one step — an empty plan would start a run with
    // nothing to do.
    private bool _HasApprovableSteps() => _plan.Plan is { Steps.Count: > 0 };

    private Button _ApproveButton()
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
            _plan.Approve();
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

    // The pipeline: a scrollable column of step blocks on the left, and the active step's session (or a hint) on the right.
    private Control _BuildPipeline(AutopilotPlan plan)
    {
        var left = new Border
        {
            Width = 300,
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Background = _Brush("CockpitSecondaryBgBrush"),
            Child = new ScrollViewer { Content = _BuildBlocks(plan) },
        };
        left[DockPanel.DockProperty] = Dock.Left;

        // Awaiting the operator (AC-155): the run parked on a blockade — show the question and an answer box instead of
        // the running session. Otherwise the live step session (under an intervene bar), or a hint between steps.
        var right = new Border
        {
            Padding = _plan.Phase == AutopilotPlanPhase.AwaitingOperator || _stepView is null ? new Thickness(16) : new Thickness(0),
            Child = _plan.Phase == AutopilotPlanPhase.AwaitingOperator
                ? _BuildBlockadePanel()
                : _stepView is { } stepView
                    ? _BuildStepSurface(stepView)
                    : _plan.ActiveStep is { } active
                        ? _CentredHint(MaterialIconKind.PlayCircleOutline, active.Title, active.Description)
                        : _CentredHint(MaterialIconKind.RobotOutline, "Waiting for the next step", "The running step's live session shows here."),
        };

        return new DockPanel { LastChildFill = true, Children = { left, right } };
    }

    // The running step's session under an intervene bar (AC-174): the step runs autonomously with its composer off, so a
    // bar over it says so and offers one button that hands the operator the keyboard (EnableCurrentStepInput). Kept a
    // thin affordance — the operator stays out of the loop unless they choose to step in.
    private Control _BuildStepSurface(Control stepView)
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
        intervene.Click += (_, _) => _coordinator.EnableCurrentStepInput();

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
    private Control _BuildBlockadePanel()
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
                _ = _coordinator.AnswerBlockadeAsync(text);
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
                            Text = _plan.PendingQuestion ?? "The run is blocked and needs your answer.",
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
        if (_StepStatusText(step.Status) is { Length: > 0 } statusText)
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

    // A short status word for a step, so the operator reads its state as words rather than a dot colour. Empty for a
    // pending step — a queued step needs no label.
    private static string _StepStatusText(AutopilotStepStatus status) => status switch
    {
        AutopilotStepStatus.Running => "Running…",
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

    private Control _BuildEmptyState() =>
        _CentredHint(MaterialIconKind.RobotOutline, "No plan yet", "Start Autopilot on an issue, or plan from scratch with the CEO — the pipeline lands here.");

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
