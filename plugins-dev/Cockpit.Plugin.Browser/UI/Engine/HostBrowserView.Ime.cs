using Avalonia;
using Avalonia.Input.TextInput;
using Avalonia.Threading;
using Cockpit.Plugin.Browser.Ipc;

namespace Cockpit.Plugin.Browser.UI.Engine;

internal sealed partial class HostBrowserView
{
    private readonly ImeClient _ime;

    private sealed class ImeClient(HostBrowserView owner) : TextInputMethodClient
    {
        private long _nextId;
        private long _id;
        private long _update;
        private Rect _cursor = new(0, 0, 1, 1);

        public override Visual TextViewVisual => owner;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => string.Empty;
        public override Rect CursorRectangle => _cursor;
        public override TextSelection Selection { get; set; }
        internal bool IsComposing => _id != 0;

        public override void SetPreeditText(string? preeditText) => SetPreeditText(preeditText, null);

        public override void SetPreeditText(string? preeditText, int? cursorPos)
        {
            if (owner.IsFocused == false || owner._closing || owner._faulted)
            {
                return;
            }
            long update = ++_update;
            if (string.IsNullOrEmpty(preeditText))
            {
                // Avalonia IMM32 clears preedit just before delivering the committed text in the same callback.
                Dispatcher.UIThread.Post(() =>
                {
                    if (_update == update)
                    {
                        Cancel(reset: false);
                    }
                }, DispatcherPriority.Background);
                return;
            }
            owner.OnOperatorInput();
            string kind = _id == 0 ? "start" : "update";
            if (_id == 0)
            {
                _id = ++_nextId;
            }
            owner._Queue("tab.ime", new BrowserTabIme(kind, _id, preeditText, Math.Clamp(cursorPos ?? preeditText.Length, 0, preeditText.Length)));
        }

        internal bool Commit(string text)
        {
            if (_id == 0)
            {
                return false;
            }
            owner._Queue("tab.ime", new BrowserTabIme("commit", _id, text));
            _id = 0;
            ++_update;
            return true;
        }

        internal void Cancel(bool reset = true)
        {
            bool composing = _id != 0;
            if (_id != 0)
            {
                long id = _id;
                _id = 0;
                owner._Queue("tab.ime", new BrowserTabIme("cancel", id));
            }
            ++_update;
            _cursor = new Rect(0, 0, 1, 1);
            if (reset && composing)
            {
                RequestReset();
            }
        }

        internal void Present(BrowserTabImeRange range)
        {
            if (range.Id != _id || _id == 0 || owner.IsFocused == false || range.SizeRevision != owner._size.Revision || range.Scale != owner._size.Scale || range.Bounds.Length == 0)
            {
                return;
            }
            int cursor = Math.Clamp(range.Cursor, 0, range.Bounds.Length);
            var character = range.Bounds[Math.Min(cursor, range.Bounds.Length - 1)];
            double x = cursor == range.Bounds.Length ? character.X + character.Width : character.X;
            _cursor = new Rect(x / range.Scale, character.Y / range.Scale, 1, character.Height / range.Scale);
            RaiseCursorRectangleChanged();
        }
    }
}
