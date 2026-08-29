using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// A message typed while a turn was in flight, held in the session's send queue (T8) and shown as a
// cancellable chip above the input. The CLI does not accept mid-turn input, so the cockpit holds the
// message locally and dispatches it when the current turn completes; removing the chip cancels it.
public partial class QueuedMessageViewModel : ViewModelBase
{
    private readonly Action<QueuedMessageViewModel> _onRemove;

    // The queued message text, sent verbatim when this entry is dispatched.
    public string Text { get; }

    // Images pasted alongside the queued text, sent with it when dispatched.
    public IReadOnlyList<ImageAttachment> Images { get; }

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
    {
        Text = text;
        Images = images;
        ReplyTo = replyTo;
        _onRemove = onRemove;
        DisplayText = _BuildDisplay(text, images.Count);
    }

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
