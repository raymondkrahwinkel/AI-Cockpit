using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.ViewModels;

// A message typed while a turn was in flight, held in the session host's queue (T8, AC-1376) and shown as a
// cancellable chip above the input; the CLI takes no mid-turn input, so it leaves when the current turn completes.
public partial class QueuedMessageViewModel : QueuedPrompt
{
    private readonly Action<QueuedMessageViewModel> _onRemove;

    // The message this entry replies to (AC-935), carried through the queue so the relation survives a turn
    // in flight instead of being lost the moment Send moves the composer's reply target off screen.
    public TranscriptEntryViewModel? ReplyTo { get; }

    // Chip label: the text plus an image count when the message carries attachments.
    public string DisplayText { get; }

    public QueuedMessageViewModel(
        string text,
        IReadOnlyList<ImageAttachment> images,
        TranscriptEntryViewModel? replyTo,
        Action<QueuedMessageViewModel> onRemove)
        : base(text, images)
    {
        ReplyTo = replyTo;
        _onRemove = onRemove;
        DisplayText = _BuildDisplay(text, images.Count);
    }

    // AC-935: the wire text carries the reply's citation; the chip and the echoed row show the bare text.
    public override string OutgoingText => SessionViewModel.BuildOutgoingText(Text, ReplyTo);

    [RelayCommand]
    private void Remove() => _onRemove(this);

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
