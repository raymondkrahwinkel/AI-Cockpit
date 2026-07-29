using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugin.LocalCi.Runtime;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.LocalCi.Ui;

/// <summary>
/// The plugin's settings view (all code-behind Avalonia, like the other plugins): what this machine can run, and
/// what to do about the part it cannot. One line per runtime rather than one line for both — the two failures are
/// unrelated and their remedies are different, so a combined line would tell the operator to do two things at once
/// or, worse, only the first.
/// </summary>
/// <remarks>
/// The probe never runs on the UI thread's back: the control renders immediately with "Checking…", the detection
/// runs against a runner with its own deadline, and the lines are filled when the answer arrives. Opening this page
/// with a dead Docker pipe costs the operator nothing but a five-second wait for one line to settle.
/// </remarks>
internal sealed class LocalCiSettingsControl : UserControl, IPluginSettingsView
{
    private readonly ILocalCiRuntime _runtime;
    private readonly LocalCiSettings _settings;
    private readonly TextBlock _dockerLine = ProviderConfigStatus.CreateLine();
    private readonly TextBlock _actLine = ProviderConfigStatus.CreateLine();
    private readonly TextBox _runnerImage;
    private readonly CheckBox _mcpEnabled;
    private readonly Button _checkAgain;

    public LocalCiSettingsControl(ILocalCiRuntime runtime, LocalCiSettings settings)
    {
        _runtime = runtime;
        _settings = settings;

        _checkAgain = new Button { Content = "Check again", Margin = new(0, 12, 0, 0) };
        _checkAgain.Click += (_, _) => _ = _CheckAsync(invalidateFirst: true);

        _runnerImage = new TextBox
        {
            PlaceholderText = ActRunOptions.DefaultRunnerImage,
            Text = settings.RunnerImage,
        };

        _mcpEnabled = new CheckBox
        {
            Content = "Offer the cockpit-local-ci tools to sessions",
            IsChecked = settings.McpEnabled,
            Margin = new(0, 16, 0, 0),
        };

        Content = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "Running a workflow job on this machine needs two things: a Docker engine that runs Linux "
                        + "containers, and the act runtime that reads the workflow and drives it.",
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new(0, 0, 0, 8),
                },
                new TextBlock { Text = "Docker" },
                _dockerLine,
                new TextBlock { Text = "act", Margin = new(0, 8, 0, 0) },
                _actLine,
                _checkAgain,
                new TextBlock { Text = "Runner image", Margin = new(0, 16, 0, 0) },
                _runnerImage,
                new TextBlock
                {
                    Text = "The image a Linux job runs in. act's images are not GitHub's runner images, so a job that "
                        + "needs a tool the default image lacks can be pointed at a bigger one here. Blank uses "
                        + ActRunOptions.DefaultRunnerImage + ".",
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                },
                _mcpEnabled,
                new TextBlock
                {
                    Text = "A session can then start these checks on its own project and read the verdict back. Every "
                        + "run still asks you to approve the exact command first.",
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        _ = _CheckAsync(invalidateFirst: false);
    }

    public bool Save()
    {
        _settings.RunnerImage = _runnerImage.Text ?? string.Empty;
        _settings.McpEnabled = _mcpEnabled.IsChecked ?? true;
        return true;
    }

    private async Task _CheckAsync(bool invalidateFirst)
    {
        if (invalidateFirst)
        {
            _runtime.Invalidate();
        }

        _checkAgain.IsEnabled = false;
        _dockerLine.Text = "Checking…";
        _actLine.Text = "Checking…";

        try
        {
            var status = await _runtime.GetStatusAsync();
            ProviderConfigStatus.Set(_dockerLine, status.Docker.Message, status.Docker.IsReady);
            ProviderConfigStatus.Set(_actLine, status.Act.Message, status.Act.IsInstalled);
        }
        catch (Exception exception)
        {
            // Nothing is awaiting this task, so an escaping exception would leave both lines reading "Checking…"
            // forever with no trace of why. Say what went wrong instead.
            ProviderConfigStatus.Set(_dockerLine, $"The check could not be run: {exception.Message}", isOk: false);
            ProviderConfigStatus.Set(_actLine, "Not checked.", isOk: false);
        }
        finally
        {
            _checkAgain.IsEnabled = true;
        }
    }
}
