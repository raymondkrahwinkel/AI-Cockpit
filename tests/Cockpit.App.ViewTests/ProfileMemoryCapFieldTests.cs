using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
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
    });
}
