using Cockpit.Plugin.Browser.Ipc;
using Xilium.CefGlue;

namespace Cockpit.Plugin.Browser.Host;

internal sealed partial class BrowserHostTab
{
    private long _imeId;
    private long _lastImeId;
    private int _imeCursor;

    private void _Ime(CefBrowserHost host, BrowserTabIme composition)
    {
        if (composition.Id <= 0 || composition.Text is null || composition.Text.Length > 32768 ||
            composition.Cursor < 0 || composition.Cursor > composition.Text.Length ||
            composition.Kind is not ("start" or "update" or "commit" or "cancel"))
        {
            throw new InvalidDataException("Invalid browser composition.");
        }
        if (_focused == false)
        {
            return;
        }
        if (composition.Kind == "start")
        {
            if (composition.Id <= _lastImeId)
            {
                return;
            }
            _CancelIme(host);
            _lastImeId = composition.Id;
            _imeId = composition.Id;
        }
        if (_imeId != composition.Id)
        {
            return;
        }
        _TakeOver();
        switch (composition.Kind)
        {
            case "start":
            case "update":
                _imeCursor = composition.Cursor;
                var underline = new CefCompositionUnderline { Range = new CefRange(0, composition.Text.Length), Color = new CefColor(255, 0, 0, 0) };
                host.ImeSetComposition(composition.Text, 1, underline, new CefRange(-1, -1), new CefRange(composition.Cursor, composition.Cursor));
                break;
            case "commit":
                _imeId = 0;
                host.ImeCommitText(composition.Text, new CefRange(-1, -1), 0);
                _State(_state with { Ime = null });
                break;
            case "cancel":
                _CancelIme(host);
                break;
        }
    }

    private void _CancelIme(CefBrowserHost host)
    {
        if (_imeId != 0)
        {
            _imeId = 0;
            host.ImeCancelComposition();
            _State(_state with { Ime = null });
        }
    }

    private void _ImeRange(CefRange range, CefRectangle[] bounds)
    {
        if (_imeId == 0 || _closing || _focused == false || bounds.Length > 32768)
        {
            return;
        }
        // CEF reports view coordinates; IPC carries device pixels with the size revision that defines them.
        double scale = _size.Scale;
        var rectangles = bounds.Select(rect => new BrowserTabImeRect(rect.X * scale, rect.Y * scale, rect.Width * scale, rect.Height * scale)).ToArray();
        _State(_state with { Ime = new BrowserTabImeRange(_imeId, _size.Revision, range.From, range.To, _imeCursor, scale, rectangles) });
    }
}
