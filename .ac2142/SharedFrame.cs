using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Xilium.CefGlue;

[SupportedOSPlatform("windows")]
internal sealed unsafe class SharedFrame : IDisposable
{
    private const int HeaderSize = 128;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _ready;
    private readonly int _width;
    private readonly int _height;
    private readonly double _scale;
    private readonly bool _suppressSignal;
    private readonly byte* _pointer;
    private Thread? _listener;
    private volatile bool _stopping;
    private int _queued;
    private CefRectangle _deferred;
    internal WriteableBitmap? Bitmap;
    internal long Offered;
    internal long CopiedBytes;
    internal long Skipped;
    internal long Consumed;
    internal Action<nint, int, int, int, long>? FrameCopied;
    internal Action? Updated;

    public SharedFrame(string name, int width, int height, double scale, bool create, bool suppressSignal = false)
    {
        _width = width;
        _height = height;
        _scale = scale;
        _suppressSignal = suppressSignal;
        long capacity = checked(HeaderSize + (long)width * height * 4);
        _mapping = create ? MemoryMappedFile.CreateNew(name, capacity) : MemoryMappedFile.OpenExisting(name);
        _view = _mapping.CreateViewAccessor(0, capacity);
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _pointer = pointer + _view.PointerOffset;
        _mutex = new Mutex(false, name + "-mutex");
        _ready = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-ready");
    }

    internal void Publish(nint source, int width, int height, CefRectangle[] rectangles)
    {
        long frameId = Interlocked.Increment(ref Offered);
        if (width != _width || height != _height)
        {
            return;
        }
        CefRectangle dirty = _deferred;
        foreach (CefRectangle rectangle in rectangles)
        {
            dirty = _Union(dirty, rectangle);
        }
        if (_mutex.WaitOne(0) == false)
        {
            _deferred = dirty;
            Interlocked.Increment(ref Skipped);
            return;
        }
        try
        {
            if (*(long*)_pointer == 0)
            {
                dirty = new CefRectangle(0, 0, width, height);
            }
            for (int y = dirty.Y; y < dirty.Y + dirty.Height; y++)
            {
                int offset = checked((y * width + dirty.X) * 4);
                int length = checked(dirty.Width * 4);
                Buffer.MemoryCopy((byte*)source + offset, _pointer + HeaderSize + offset, length, length);
                CopiedBytes += length;
            }
            if (*(int*)(_pointer + 8) != 0)
            {
                dirty = _Union(dirty, new CefRectangle(*(int*)(_pointer + 16), *(int*)(_pointer + 20), *(int*)(_pointer + 24), *(int*)(_pointer + 28)));
            }
            *(long*)_pointer = frameId;
            *(int*)(_pointer + 8) = 1;
            *(int*)(_pointer + 16) = dirty.X;
            *(int*)(_pointer + 20) = dirty.Y;
            *(int*)(_pointer + 24) = dirty.Width;
            *(int*)(_pointer + 28) = dirty.Height;
            *(long*)(_pointer + 40) = CopiedBytes;
            *(long*)(_pointer + 48) = Offered;
            *(long*)(_pointer + 56) = Skipped;
            _deferred = default;
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
        if (_suppressSignal == false)
        {
            _ready.Set();
        }
    }

    internal void Listen()
    {
        _listener = new Thread(() =>
        {
            while (_stopping == false)
            {
                _ready.WaitOne();
                if (_stopping == false && Interlocked.Exchange(ref _queued, 1) == 0)
                {
                    Dispatcher.UIThread.Post(_Consume, DispatcherPriority.Render);
                }
            }
        }) { IsBackground = true };
        _listener.Start();
    }

    private void _Consume()
    {
        Interlocked.Exchange(ref _queued, 0);
        _mutex.WaitOne();
        try
        {
            if (*(int*)(_pointer + 8) == 0)
            {
                return;
            }
            Bitmap ??= new WriteableBitmap(new PixelSize(_width, _height), new Vector(96 * _scale, 96 * _scale), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using ILockedFramebuffer frame = Bitmap.Lock();
            int x = *(int*)(_pointer + 16);
            int y = *(int*)(_pointer + 20);
            int width = *(int*)(_pointer + 24);
            int height = *(int*)(_pointer + 28);
            for (int row = y; row < y + height; row++)
            {
                int length = width * 4;
                Buffer.MemoryCopy(_pointer + HeaderSize + (row * _width + x) * 4, (byte*)frame.Address + row * frame.RowBytes + x * 4, length, length);
                CopiedBytes += length;
            }
            Consumed++;
            FrameCopied?.Invoke(frame.Address, _width, _height, frame.RowBytes, *(long*)_pointer);
            *(int*)(_pointer + 8) = 0;
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
        Updated?.Invoke();
    }

    internal (long HostBytes, long Offered, long Skipped) Counters()
    {
        _mutex.WaitOne();
        try
        {
            return (*(long*)(_pointer + 40), *(long*)(_pointer + 48), *(long*)(_pointer + 56));
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
    }

    private static CefRectangle _Union(CefRectangle first, CefRectangle second)
    {
        if (first.Width == 0 || first.Height == 0)
        {
            return second;
        }
        int x = Math.Min(first.X, second.X);
        int y = Math.Min(first.Y, second.Y);
        return new CefRectangle(x, y, Math.Max(first.X + first.Width, second.X + second.Width) - x, Math.Max(first.Y + first.Height, second.Y + second.Height) - y);
    }

    public void Dispose()
    {
        _stopping = true;
        _ready.Set();
        _listener?.Join();
        Bitmap?.Dispose();
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mapping.Dispose();
        _ready.Dispose();
        _mutex.Dispose();
    }
}
