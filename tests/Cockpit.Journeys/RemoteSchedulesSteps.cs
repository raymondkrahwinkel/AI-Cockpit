using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Cockpit.App.Services;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Infrastructure.BackendApi;
using Microsoft.Extensions.Logging;

namespace Cockpit.Journeys;

internal static class RemoteSchedulesSteps
{
    public static async Task RunAsync(BackendApiClient server, IRemoteServers servers, ILoggerFactory loggers, string localRoot)
    {
        var localBefore = await File.ReadAllBytesAsync(Path.Combine(localRoot, "cockpit.json"));
        await Cockpit.App.ViewTests.HeadlessAvalonia.RunAsync(async () =>
        {
            var window = new MainWindow(windowBoundsStore: null, "journey-server");
            using var ui = new RemoteWindowUi(window, new SurfaceWindows(), loggers);
            var remote = CockpitViewModel.ForRemoteWindow("journey-server", servers, null, null, _ => Task.CompletedTask, ui: ui);
            window.DataContext = remote;
            window.Show();
            Window? schedules = null;
            try
            {
                await ui.InitializeAsync(remote, servers, "journey-server", localRoot);
                schedules = await OpenAsync(remote);
                var screen = schedules.GetLogicalDescendants().OfType<Control>().Single(control => control.GetType().Name == "SchedulesScreen");
                Assert.Equal("journey-server", Property(screen, "Server"));
                Click(schedules, "+ New task");
                var editor = (Control)Property(screen, "Editor");
                Call(editor, "SetWeekly", "07:30", new[] { "1" });
                var fixedMessage = editor.GetLogicalDescendants().OfType<RadioButton>().Single(button => button.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Fixed message"));
                fixedMessage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Box(editor, "e.g. Time for the monthly investor review.").Text = "AC2289 server result";
                Box(editor, "e.g. Weekly backup check").Text = "AC2289 acceptance task";
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await (Task)Call(editor, "PreviewSettledAsync");
                await (Task)Call(editor, "SaveAsync");
                var state = Property(screen, "State");
                object TaskRow() => ((IEnumerable)Property(state, "Tasks")).Cast<object>().Select(row => Property(row, "Task")).Single(task => Equals(Property(task, "Name"), "AC2289 acceptance task"));
                Assert.True(((IEnumerable)Property(state, "Tasks")).Cast<object>().Any(row => Equals(Property(Property(row, "Task"), "Name"), "AC2289 acceptance task")), string.Join(" | ", remote.ToastHost.Toasts.Select(toast => toast.Message)));
                Console.WriteLine("AC2289: task created from remote editor");
                var task = TaskRow();
                var id = (string)Property(task, "Id");
                await (Task)Call(Property(screen, "Tasks"), "ToggleAsync", task, null);
                Assert.False((bool)Property(TaskRow(), "IsEnabled"));
                await (Task)Call(Property(screen, "Tasks"), "RunNowAsync", TaskRow());
                await SnapshotHoldsAsync(snapshot => Runs(snapshot, id).Any(run => run?["outcome"]?.ToString() == "Succeeded"));
                Call(screen, "Edit", TaskRow());
                editor = (Control)Property(screen, "Editor");
                Box(editor, "e.g. Time for the monthly investor review.").Text = "AC2289 edited result";
                await (Task)Call(editor, "PreviewSettledAsync");
                await (Task)Call(editor, "SaveAsync");
                Assert.Equal("AC2289 edited result", Property(Property(TaskRow(), "Action"), "Text"));

                Console.WriteLine("AC2289: task paused, manually run and edited");
                Call(screen, "Show", "reminders");
                foreach (var text in new[] { "AC2289 snooze", "AC2289 complete" })
                {
                    Box(schedules, "Reminder").Text = text;
                    Box(schedules, "once yyyy-MM-dd HH:mm or cron").Text = "once " + DateTimeOffset.UtcNow.AddMinutes(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    Box(schedules, "Time zone").Text = "UTC";
                    Click(schedules, "Create reminder");
                    await SnapshotHoldsAsync(snapshot => Reminders(snapshot).Any(reminder => reminder?["text"]?.ToString() == text));
                }
                await SnapshotHoldsAsync(snapshot => Reminders(snapshot).Count(reminder => reminder?["state"]?.ToString() == "Due" && reminder?["text"]?.ToString().StartsWith("AC2289", StringComparison.Ordinal) == true) == 2);
                await Until.LayoutHolds(schedules, () => schedules.GetLogicalDescendants().OfType<Button>().Count(button => Equals(button.Content, "Snooze 10 min")) == 2);
                Control ReminderRow(string text) => schedules.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Text == text).Parent as Control ?? throw new InvalidOperationException("No reminder row.");
                Click(ReminderRow("AC2289 snooze"), "Snooze 10 min");
                Click(ReminderRow("AC2289 complete"), "Done");
                await SnapshotHoldsAsync(snapshot => Reminders(snapshot).Any(reminder => reminder?["text"]?.ToString() == "AC2289 snooze" && reminder?["snoozedUntil"] is not null)
                    && Reminders(snapshot).Any(reminder => reminder?["text"]?.ToString() == "AC2289 complete" && reminder?["state"]?.ToString() == "Done"));
                Console.WriteLine("AC2289: reminders created, due, snoozed and completed through remote UI");
                SaveRender(schedules, "reminders.png");

                Call(screen, "Show", "tasks");
                Call(screen, "Edit", TaskRow());
                editor = (Control)Property(screen, "Editor");
                var frequency = editor.GetLogicalDescendants().OfType<ComboBox>().FirstOrDefault(box => box.Items.Cast<object>().Contains("Once"));
                if (frequency is null)
                {
                    Call(editor, "SetPlain");
                    frequency = editor.GetLogicalDescendants().OfType<ComboBox>().Single(box => box.Items.Cast<object>().Contains("Once"));
                }
                frequency.SelectedIndex = 0;
                var slot = DateTimeOffset.UtcNow.AddMinutes(1);
                editor.GetLogicalDescendants().OfType<TextBox>().Single(box => box.Width == 70).Text = slot.ToString("HH:mm", CultureInfo.InvariantCulture);
                editor.GetLogicalDescendants().OfType<TextBox>().Single(box => box.Width == 110).Text = slot.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                editor.GetLogicalDescendants().OfType<ComboBox>().Single(box => box.Items.Cast<object>().Contains("UTC")).SelectedItem = "UTC";
                await (Task)Call(editor, "PreviewSettledAsync");
                await (Task)Call(editor, "SaveAsync");
                await (Task)Call(Property(screen, "Tasks"), "ToggleAsync", TaskRow(), null);
                schedules.Close();
                schedules = null;
                window.Close();
                ui.Dispose();
                await SnapshotHoldsAsync(snapshot => Runs(snapshot, id).Count(run => run?["trigger"]?.ToString() == "Schedule" && run?["outcome"]?.ToString() == "Succeeded") == 1);

                Console.WriteLine("AC2289: scheduled task succeeded with both client windows closed");
                var reopenedWindow = new MainWindow(windowBoundsStore: null, "journey-server");
                using var reopenedUi = new RemoteWindowUi(reopenedWindow, new SurfaceWindows(), loggers);
                var reopened = CockpitViewModel.ForRemoteWindow("journey-server", servers, null, null, _ => Task.CompletedTask, ui: reopenedUi);
                reopenedWindow.DataContext = reopened;
                reopenedWindow.Show();
                try
                {
                    await reopenedUi.InitializeAsync(reopened, servers, "journey-server", localRoot);
                    schedules = await OpenAsync(reopened);
                    screen = schedules.GetLogicalDescendants().OfType<Control>().Single(control => control.GetType().Name == "SchedulesScreen");
                    Call(screen, "Show", "runs");
                    await Until.LayoutHolds(schedules, () => schedules.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "AC2289 acceptance task"));
                    SaveRender(schedules, "history-after-reopen.png");
                    var snapshot = await SnapshotAsync();
                    Assert.Single(Runs(snapshot, id), run => run?["trigger"]?.ToString() == "Schedule");
                    Assert.Equal(2, Runs(snapshot, id).Count());
                    Console.WriteLine("AC2289: reopened history has one manual and exactly one scheduled result");
                }
                finally
                {
                    schedules?.Close();
                    schedules = null;
                    reopenedWindow.Close();
                }
            }
            finally
            {
                schedules?.Close();
                window.Close();
            }
        });
        Assert.Equal(localBefore, await File.ReadAllBytesAsync(Path.Combine(localRoot, "cockpit.json")));

        Task<JsonObject> SnapshotAsync() => server.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/plugins/schedules/channel/snapshot", new { });
        async Task SnapshotHoldsAsync(Func<JsonObject, bool> condition)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
            while (!condition(await SnapshotAsync().WaitAsync(deadline.Token)))
            {
                await Task.Delay(150, deadline.Token);
            }
        }
    }

    private static IEnumerable<JsonNode?> Runs(JsonObject snapshot, string id) => (snapshot["runs"]?.AsArray() ?? []).Where(run => run?["taskId"]?.ToString() == id);
    private static IEnumerable<JsonNode?> Reminders(JsonObject snapshot) => snapshot["reminders"]?.AsArray() ?? [];
    private static object Property(object target, string name) => target.GetType().GetProperty(name)?.GetValue(target) ?? throw new InvalidOperationException($"Missing {name}.");
    private static object Call(object target, string name, params object?[] args) => (target.GetType().GetMethod(name) ?? throw new InvalidOperationException($"Missing method {name}.")).Invoke(target, args) ?? Task.CompletedTask;
    private static TextBox Box(Control control, string placeholder) => control.GetLogicalDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == placeholder);
    private static void Click(Control control, string label) => control.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task<Window> OpenAsync(CockpitViewModel remote)
    {
        var opened = new TaskCompletionSource<Window>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (window.Title == "Schedules")
            {
                opened.TrySetResult(window);
            }
        });
        remote.PluginSideButtons.Single(button => button.Title == "Schedules").OnInvoke();
        return await opened.Task.WaitAsync(Until.Ceiling);
    }

    private static void SaveRender(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("AC2289_REVIEW_DIR");
        if (directory is null)
        {
            return;
        }
        Directory.CreateDirectory(directory);
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No acceptance render.");
        frame.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }
}
