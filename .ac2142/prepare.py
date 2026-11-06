"""Prepare the disposable source reference and verified CEF runtime in this session's scratch."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import urllib.request

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parent.parent
PROBE = ROOT / ".ac2142"
SCRATCH = Path("C:/ck/487726/ac2142")
SOURCE = ROOT / "plugins-dev/Cockpit.Plugin.Browser/UI"


def main() -> None:
    generated = PROBE / "generated"
    generated.mkdir(exist_ok=True)
    source = (SOURCE / "Engine/CefBrowserView.cs").read_text(encoding="utf-8")
    source = source.replace("    private int _blitQueued;", """    private int _blitQueued;
    internal Action<nint, int, int, CefRectangle[]>? PaintOverride;
    internal Action<nint, int, int, int, long>? FrameCopied;
    internal long PaintCount;
    internal long CopyBytes;
    internal long BufferAllocations;
    private long _backFrameId;
    private long _frontFrameId;
    internal CefBrowser? ProbeBrowser => _browser;
    internal void ProbeSize(int width, int height, double scale)
    {
        _viewSize = new Size(width, height);
        _scale = scale;
        _browser?.GetHost().NotifyScreenInfoChanged();
        _browser?.GetHost().WasResized();
    }
""")
    source = source.replace("            _backSize = new PixelSize(width, height);", "            _backSize = new PixelSize(width, height);\n            _backFrameId = PaintCount;")
    source = source.replace("                _back = new byte[length];", "                _back = new byte[length];\n                BufferAllocations++;")
    source = source.replace("            Marshal.Copy(buffer, _back, 0, length);", "            Marshal.Copy(buffer, _back, 0, length);\n            Interlocked.Add(ref CopyBytes, length);")
    source = source.replace("            size = _backSize;", "            size = _backSize;\n            (_frontFrameId, _backFrameId) = (_backFrameId, _frontFrameId);")
    needle = "            Marshal.Copy(_front, 0, frame.Address, Math.Min(size.Width * size.Height * 4, frame.RowBytes * size.Height));"
    assert source.count(needle) == 1
    source = source.replace(needle, needle + "\n            Interlocked.Add(ref CopyBytes, size.Width * (long)size.Height * 4);\n            FrameCopied?.Invoke(frame.Address, size.Width, size.Height, frame.RowBytes, _frontFrameId);")
    source = source.replace("                owner._Paint(buffer, width, height);", """                Interlocked.Increment(ref owner.PaintCount);
                if (owner.PaintOverride is { } paint)
                {
                    paint(buffer, width, height, dirtyRects);
                }
                else
                {
                    owner._Paint(buffer, width, height);
                }""")
    (generated / "CurrentView.cs").write_text(source, encoding="utf-8")
    shutil.copyfile(SOURCE / "Engine/KeyCodes.cs", generated / "KeyCodes.cs")
    catalog = json.loads((SOURCE / "Runtime/cef-catalog.json").read_text())
    package = catalog["packages"]["win-x64"]
    SCRATCH.mkdir(parents=True, exist_ok=True)
    archive = SCRATCH / "cef.tar.bz2"
    if not archive.exists():
        urllib.request.urlretrieve(package["url"], archive)
    assert hashlib.file_digest(archive.open("rb"), "sha256").hexdigest() == package["sha256"]
    runtime = SCRATCH / "cef"
    runtime.mkdir(exist_ok=True)
    if not (runtime / "libcef.dll").exists():
        with tarfile.open(archive) as bundle:
            for member in bundle:
                parts = Path(member.name).parts
                if len(parts) < 3 or parts[1] not in ("Release", "Resources") or not member.isfile():
                    continue
                target = runtime.joinpath(*parts[2:])
                target.parent.mkdir(parents=True, exist_ok=True)
                stream = bundle.extractfile(member)
                if stream is None:
                    raise RuntimeError(member.name)
                with stream, target.open("wb") as destination:
                    shutil.copyfileobj(stream, destination)
    print(f"CEF verified: {runtime}; reference generated from {subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()}")


if __name__ == "__main__":
    main()
