using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// A message typed while a turn was in flight, held in the session's send queue (T8) and shown as a
/// cancellable chip above the input. The CLI does not accept mid-turn input, so the cockpit holds the
/// message locally and dispatches it when the current turn completes; removing the chip cancels it.
/// </summary>
public partial class QueuedMessageViewModel : ViewModelBase
{
    private readonly Action<QueuedMessageViewModel> _onRemove;

    /// <summary>The queued message text, sent verbatim when this entry is dispatched.</summary>
    public string Text { get; }

    /// <summary>Images pasted alongside the queued text, sent with it when dispatched.</summary>
    public IReadOnlyList<ImageAttachment> Images { get; }

    /// <summary>Chip label: the text plus an image count when the message carries attachments.</summary>
    public string DisplayText { get; }

    public QueuedMessageViewModel(string text, IReadOnlyList<ImageAttachment> images, Action<QueuedMessageViewModel> onRemove)
    {
        Text = text;
        Images = images;
        _onRemove = onRemove;
        DisplayText = _BuildDisplay(text, images.Count);
    }

    [RelayCommand]
    private void Remove() => _onRemove(this);

    private static string _BuildDisplay(string text, int imageCount)
    {
        var suffix = imageCount == 0 ? string.Empty : $"[+{imageCount} image{(imageCount == 1 ? "" : "s")}]";
        if (string.IsNullOrWhiteSpace(text))
        {
            return suffix;
        }

        return imageCount == 0 ? text : $"{text}  {suffix}";
    }
}
