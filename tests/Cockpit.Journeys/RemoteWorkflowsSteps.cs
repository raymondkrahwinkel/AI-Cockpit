using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.App.ViewModels;

namespace Cockpit.Journeys;

internal static class RemoteWorkflowsSteps
{
    internal static async Task RunAsync(Window window, Func<Task<Window>> open, BackendApiClient server, IRemoteServers servers, string localRoot)
    {
        var output = Path.Combine(Path.GetDirectoryName(localRoot) ?? throw new InvalidOperationException("No fixture root."), "ac2290-output.txt");
        Window? flows = await open();
        try
        {
            Click(flows, "+ New flow");
            await Until.LayoutHolds(flows, () => flows.GetLogicalDescendants().Any(control => control.GetType().Name == "WorkflowEditorControl"));
            var editor = Editor(flows);
            Call(editor, "_Rename", "AC2290 remote acceptance");
            Call(editor, "_Drop", "cockpit.manual", 40d, 50d);
            Call(editor, "_Drop", "cockpit.command", 350d, 50d);
            var workflow = Field(editor, "_workflow");
            var nodes = ((IEnumerable)Property(workflow, "Nodes")).Cast<object>().ToList();
            var command = (IDictionary)Property(nodes[1], "Parameters");
            command["Command"] = $"echo AC2290-created > \"{output}\"";
            Call(workflow, "Connect", Property(nodes[0], "Id"), 0, Property(nodes[1], "Id"));
            Call(editor, "_Touched");
            await SavedAsync("AC2290-created");
            await (Task)Call(editor, "_RunAsync");
            Assert.True(File.Exists(output), $"Run status: {((TextBlock)Field(editor, "_status")).Text}; result: {System.Text.Json.JsonSerializer.Serialize(editor.GetType().GetField("_lastRun", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(editor))}");
            Assert.Equal("AC2290-created", (await File.ReadAllTextAsync(output)).Trim());
            Assert.Equal("Succeeded", Property(Field(editor, "_lastRun"), "Status").ToString());
            await RenderAsync(flows, "created-run.png");

            command["Command"] = $"echo AC2290-edited > \"{output}\"";
            Call(editor, "_Touched");
            await SavedAsync("AC2290-edited");
            await (Task)Call(editor, "_RunAsync");
            Assert.Equal("AC2290-edited", (await File.ReadAllTextAsync(output)).Trim());
            command["Command"] = OperatingSystem.IsWindows() ? "exit /b 23" : "exit 23";
            Call(editor, "_Touched");
            await (Task)Field(Dialog(flows), "_saving");
            await (Task)Call(editor, "_RunAsync");
            Assert.Equal("Failed", Property(Field(editor, "_lastRun"), "Status").ToString());
            await RenderAsync(flows, "failed-run.png");
            flows.Close();
            flows = await open();
            var manager = flows.GetLogicalDescendants().OfType<Control>().Single(control => control.GetType().Name == "WorkflowManagerControl");
            var savedFlows = ((IEnumerable)Field(manager, "_workflows")).Cast<object>();
            var saved = savedFlows.Single(flow => Equals(Property(flow, "Name"), "AC2290 remote acceptance"));
            await (Task)Call(Dialog(flows), "_OpenAsync", saved);
            editor = Editor(flows);
            Assert.Equal("Failed", Property(Field(editor, "_lastRun"), "Status").ToString());
            Assert.Contains(flows.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text?.Contains("23", StringComparison.Ordinal) == true);
            await RenderAsync(flows, "history-reopened.png");

            var administration = servers.Servers.Single(target => target.Name == "journey-server").Plugins;
            var client = administration.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => field.FieldType == typeof(BackendApiClient)).GetValue(administration) ?? throw new InvalidOperationException("No remote API client.");
            var http = (HttpClient)Field(client, "_http");
            var authorization = http.DefaultRequestHeaders.Authorization;
            var stored = await server.SendAsync<string>(HttpMethod.Post, "api/v1/plugins/workflows/channel/load", new { });
            http.DefaultRequestHeaders.Authorization = null;
            try
            {
                var before = await File.ReadAllBytesAsync(output);
                await (Task)Call(editor, "_RunAsync");
                Assert.StartsWith("Could not run this flow:", ((TextBlock)Field(editor, "_status")).Text);
                Assert.Equal(before, await File.ReadAllBytesAsync(output));
                await RenderAsync(flows, "channel-unavailable.png");
                Call(editor, "_Rename", "AC2290 must not save locally");
                await Assert.ThrowsAnyAsync<Exception>(async () => await (Task)Field(Dialog(flows), "_saving"));
                await Until.LayoutHolds(window, () => (window.DataContext as CockpitViewModel ?? throw new InvalidOperationException("No remote view model.")).ToastHost.Toasts.Any(toast => toast.Message.StartsWith("Could not save your flows:", StringComparison.Ordinal)));
                await RenderAsync(window, "save-unavailable.png");
            }
            finally
            {
                http.DefaultRequestHeaders.Authorization = authorization;
            }
            Assert.Equal(stored, await server.SendAsync<string>(HttpMethod.Post, "api/v1/plugins/workflows/channel/load", new { }));
            Console.WriteLine("AC2290: remote create/edit/write/run/failure/reopen/channel-loss accepted through app UI");
        }
        finally
        {
            flows?.Close();
        }

        async Task SavedAsync(string expected)
        {
            string saved = "";
            await Until.ReloadHolds(async () => saved = await server.SendAsync<string>(HttpMethod.Post, "api/v1/plugins/workflows/channel/load", new { }),
                () => saved.Contains(expected, StringComparison.Ordinal));
        }
    }

    private static Control Editor(Window window) => window.GetLogicalDescendants().OfType<Control>().Single(control => control.GetType().Name == "WorkflowEditorControl");
    private static Control Dialog(Window window) => window.GetLogicalDescendants().OfType<Control>().Single(control => control.GetType().Name == "WorkflowsDialogControl");
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target) ?? throw new InvalidOperationException($"Missing field {name}.");
    private static object Property(object target, string name) => target.GetType().GetProperty(name)?.GetValue(target) ?? throw new InvalidOperationException($"Missing property {name}.");
    private static object Call(object target, string name, params object?[] arguments) => (target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new InvalidOperationException($"Missing method {name}.")).Invoke(target, arguments) ?? Task.CompletedTask;
    private static void Click(Control control, string label) => control.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task RenderAsync(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("AC2290_REVIEW_DIR") is not { } directory)
        {
            return;
        }
        Directory.CreateDirectory(directory);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No workflow acceptance render.");
        frame.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }
}
