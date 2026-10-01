using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// A message typed while a turn was in flight, held in the session host's queue (T8, AC-1376) and shown as a
// cancellable chip above the input; the CLI takes no mid-turn input, so it leaves when the current turn completes.
// AC-1438: the chip of one queued prompt, which the host holds as a Core DTO.
public partial class QueuedMessageViewModel(
    QueuedPrompt prompt,
    TranscriptEntryViewModel? replyTo,
    Action<QueuedMessageViewModel> onRemove)
{
    public QueuedPrompt Prompt { get; } = prompt;

    public string Text => Prompt.Text;

    public IReadOnlyList<ImageAttachment> Images => Prompt.Images;

    // The message this entry replies to (AC-935), carried through the queue so the relation survives a turn
    // in flight instead of being lost the moment Send moves the composer's reply target off screen.
    public TranscriptEntryViewModel? ReplyTo { get; } = replyTo;

    // Chip label: the text plus an image count when the message carries attachments.
    public string DisplayText { get; } = _BuildDisplay(prompt.Text, prompt.Images.Count);

    [RelayCommand]
    private void Remove() => onRemove(this);

    private static string _BuildDisplay(string text, int imageCount)
    {
        var suffix = ImageCountLabel.Format(imageCount);
        if (string.IsNullOrWhiteSpace(text))
        {
            return suffix;
        }

        var display = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return imageCount == 0 ? display : $"{display} {suffix}";
    }
}
