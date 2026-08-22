using System.Text.RegularExpressions;
using Cockpit.TestSupport;

namespace Cockpit.Core.Tests.Views;

// AC-1000 §2: "each existing field lands in exactly one category, in the category the mapping table says" —
// OptionsStagingGuardTests already checks every editable control's binding is classified *somewhere*; this
// checks *where*, for the fields that moved category as part of the AC-1000 reindeling (the four
// "kernverplaatsingen": hotkeys together, consent to Security, assistant split from voice, nodes split from
// security) plus one representative from a category left otherwise untouched.
public class OptionsDialogCategoryMappingTests
{
    private static readonly string DialogMarkup =
        File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", "Cockpit.App", "Views", "OptionsDialog.axaml"));

    // Category key -> the exact markup of that category's content page, from its own root element's
    // `Tag="key"` (a `ScrollViewer` for every category except Profiles, which AC-1019 rooted on a `Grid` instead
    // so its list and detail columns can scroll independently) up to (not including) the next category's root,
    // or the end of the sidebar/content Panel for the last one. The sidebar's ListBoxItems also carry a matching
    // Tag, but they sit before every one of these matches, so they never split a span.
    private static readonly Dictionary<string, string> CategorySpans = _SplitIntoCategorySpans();

    [Theory]
    [InlineData("sessions", "AutoCloseOnExit")]
    [InlineData("profiles", "Profiles.AddProfileCommand")]
    [InlineData("appearance", "GlobalSingleSessionLayout")]
    [InlineData("terminal", "SelectedTerminalShell")]
    [InlineData("notifications", "LocalNotificationsEnabled")]
    [InlineData("shortcuts", "ScreenshotHotkeyKeyName")]
    [InlineData("shortcuts", "VoicePushToTalkKeyName")]
    [InlineData("shortcuts", "AssistantOptions.PushToTalkKeyName")]
    [InlineData("voice", "VoiceEnabled")]
    [InlineData("voice", "VoiceStopReadAloudWhenSpeaking")]
    [InlineData("assistant", "AssistantOptions.IsEnabled")]
    [InlineData("assistant", "SelectedTtsVoice")]
    [InlineData("security", "Security.IsEncrypted")]
    [InlineData("security", "AssistantOptions.ConsentBypassAll")]
    [InlineData("nodes", "Security.NodeEndpointEnabled")]
    [InlineData("backup", "BackupIncludesCredentials")]
    [InlineData("updates", "CheckForUpdatesOnStartup")]
    [InlineData("debug", "ShowDebugControls")]
    public void FieldLandsInItsExpectedCategory(string category, string bindingPath)
    {
        Assert.Contains(bindingPath, CategorySpans[category], StringComparison.Ordinal);
    }

    // The four fields the reindeling actually moved: assert they are NOT still reachable from their old home.
    [Theory]
    [InlineData("voice", "AssistantOptions.PushToTalkKeyName")] // moved: Voice -> Shortcuts
    [InlineData("assistant", "AssistantOptions.ConsentBypassAll")] // moved: Assistant -> Security
    [InlineData("assistant", "VoiceStopReadAloudWhenSpeaking")] // moved: Assistant -> Voice
    [InlineData("voice", "SelectedTtsVoice")] // moved: Voice -> Assistant
    [InlineData("security", "PairWithNodeAddress")] // moved: Security -> Nodes
    public void MovedField_IsNoLongerInItsOldCategory(string oldCategory, string bindingPath)
    {
        Assert.DoesNotContain(bindingPath, CategorySpans[oldCategory], StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCategoryHasExactlyOneSpan()
    {
        string[] expected =
        [
            "sessions", "profiles", "appearance", "terminal", "notifications", "shortcuts",
            "voice", "assistant",
            "security", "nodes", "backup", "updates", "debug",
        ];

        Assert.Equal(expected.ToHashSet(), CategorySpans.Keys.ToHashSet());
    }

    private static Dictionary<string, string> _SplitIntoCategorySpans()
    {
        var matches = Regex.Matches(DialogMarkup, @"<(?:ScrollViewer|Grid)\s+Tag=""(?<key>\w+)""").ToList();
        var spans = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : DialogMarkup.Length;
            spans[matches[i].Groups["key"].Value] = DialogMarkup[start..end];
        }

        return spans;
    }
}
