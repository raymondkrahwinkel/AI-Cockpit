using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-666: the profile editor's "Memory cap (MB)" field takes numbers only. Measured against the real markup — the
/// field was a plain <c>TextBox</c>, so "4096xcxc" was accepted on screen and then parsed back to "no cap" on save,
/// which a view-model test passes straight through.
/// </summary>
[Collection("avalonia")]
public class ProfileMemoryCapFieldTests
{
    private static ManageProfilesDialogViewModel _DialogEditing(int? cap)
    {
        var row = new EditableProfileViewModel(
            new SessionProfile("work", ClaudePluginProfile.Create("/home/r/.claude-work", null)) { MemoryCapMegabytes = cap },
            isLoggedIn: true);
        var dialog = new ManageProfilesDialogViewModel();
        dialog.Profiles.Add(row);
        dialog.SelectedProfile = row;
        return dialog;
    }

    // The delegation section has NumericUpDowns of its own; this one is the only cap-sized field.
    private static NumericUpDown _CapBox(Window window) =>
        window.GetVisualDescendants().OfType<NumericUpDown>().First(box => box.Minimum == 512);

    [Fact]
    public void TypingLettersIntoTheCap_LeavesTheNumberAlone() => HeadlessAvalonia.Run(() =>
    {
        var dialog = _DialogEditing(4096);
        var window = new ManageProfilesDialog { DataContext = dialog };
        window.Show();
        try
        {
            window.UpdateLayout();
            var box = _CapBox(window);
            box.Focus();
            window.KeyTextInput("xcxc");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.UpdateLayout();

            Assert.Equal(4096, dialog.SelectedProfile!.MemoryCapMegabytes);
            Assert.Equal(4096, dialog.SelectedProfile!.ToProfile().MemoryCapMegabytes);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void AnEmptyCap_StaysTheAppDefault() => HeadlessAvalonia.Run(() =>
    {
        var dialog = _DialogEditing(4096);
        var window = new ManageProfilesDialog { DataContext = dialog };
        window.Show();
        try
        {
            window.UpdateLayout();
            _CapBox(window).Value = null;
            window.UpdateLayout();

            Assert.Null(dialog.SelectedProfile!.ToProfile().MemoryCapMegabytes);
        }
        finally
        {
            window.Close();
        }

        var row = new EditableProfileViewModel(new SessionProfile("LM Studio E", new LmStudioConfig("http://localhost:1234", "qwen/qwen3.8-27b", ContextTokens: 32_768)), true);
        dialog.Profiles.Add(row);
        dialog.SelectedProfile = row;
        using var scene = RenderedScene.Show(new ProfilesEditor { DataContext = dialog }, 980, 1550);
        T Field<T>(string name) where T : Control => scene.Window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        Assert.False(Field<CheckBox>("ManageLmStudioLoadOptions").IsChecked);
        Assert.Null(Assert.IsType<LmStudioConfig>(row.ToProfile().ProviderConfig).LoadOptions);
        Field<CheckBox>("ManageLmStudioLoadOptions").IsChecked = true;
        Field<NumericUpDown>("LmStudioGpuOffloadRatio").Value = 1;
        Field<NumericUpDown>("LmStudioParallel").Value = 1;
        Assert.Null(Field<NumericUpDown>("LmStudioEvalBatchSize").Value);
        Assert.Null(Field<NumericUpDown>("LmStudioPhysicalBatchSize").Value);
        Assert.Null(Field<NumericUpDown>("LmStudioCpuThreads").Value);
        Field<NumericUpDown>("LmStudioEvalBatchSize").Value = 43;
        Field<NumericUpDown>("LmStudioPhysicalBatchSize").Value = 42;
        Field<NumericUpDown>("LmStudioCpuThreads").Value = 44;
        Field<ComboBox>("LmStudioFlashAttention").SelectedItem = "On";
        Field<ComboBox>("LmStudioOffloadKvCacheToGpu").SelectedItem = "On";
        Field<ComboBox>("LmStudioKCacheQuantization").SelectedItem = "q8_0";
        Field<ComboBox>("LmStudioVCacheQuantization").SelectedItem = "q8_0";
        Field<ComboBox>("LmStudioDraftMtp").SelectedItem = "On";
        Field<NumericUpDown>("LmStudioDraftMaxTokens").Value = 3;
        Field<NumericUpDown>("LmStudioDraftMinTokens").Value = 0;
        Field<NumericUpDown>("LmStudioDraftMinContinueProbability").Value = 0;
        Field<ComboBox>("LmStudioThinking").SelectedItem = "On";
        scene.Window.UpdateLayout();
        var saved = Assert.IsType<LmStudioConfig>(row.ToProfile().ProviderConfig);
        Assert.Equal(new LmStudioLoadOptions(1, 1, true, "q8_0", "q8_0", true, true, DraftMaxTokens: 3, DraftMinTokens: 0, DraftMinContinueProbability: 0,
            EvalBatchSize: 43, PhysicalBatchSize: 42, CpuThreads: 44), saved.LoadOptions);
        Assert.True(saved.EnableThinking);
        Assert.True(row.IsValid);
        if (Environment.GetEnvironmentVariable("LMSTUDIO_PROFILE_RENDER_DIR") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            var app = Application.Current!;
            var originalTheme = app.RequestedThemeVariant;
            try
            {
                foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    app.RequestedThemeVariant = theme;
                    Dispatcher.UIThread.RunJobs();
                    scene.Window.UpdateLayout();
                    using var frame = scene.Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No profile frame.");
                    frame.Save(Path.Combine(folder, $"lmstudio-profile-{theme}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
            finally
            {
                app.RequestedThemeVariant = originalTheme;
            }
        }

        Field<CheckBox>("ManageLmStudioLoadOptions").IsChecked = false;
        Assert.Null(Assert.IsType<LmStudioConfig>(row.ToProfile().ProviderConfig).LoadOptions);
    });
}
