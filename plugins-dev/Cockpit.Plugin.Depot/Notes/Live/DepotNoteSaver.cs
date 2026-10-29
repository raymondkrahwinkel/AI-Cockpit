using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Notes.Live;

// The note-save answer (protocol §6 duty 3): 200 {checksum, version}, 409 {checksum, own}, 410, 422, 403; 0 when none came back.
internal sealed record DepotNoteSaveAnswer(int Status, string? Checksum = null, int? Version = null, bool Own = false);

internal delegate Task<DepotNoteSaveAnswer> DepotNotePost(string content, string baseChecksum, string session, CancellationToken cancellationToken);

internal enum DepotNoteSaveState
{
    Saving,
    Saved,
    Held,
    Failed,
}

// Depot's saver.ts in C#, in its order: serialize after 3 s of quiet (15 s at most), post with the checksum it last knew, and on a
// 409 tell a lost answer, a save of this very session and a writer outside the room apart. Only the last one holds the saves, until
// the outside write is merged and Rebase names the merged base: nothing is ever written over text from outside.
internal sealed partial class DepotNoteSaver : IDisposable
{
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(15);
    public static readonly IReadOnlyList<TimeSpan> Retry = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    private readonly Func<CancellationToken, Task<string>> _read;
    private readonly DepotNotePost _post;
    private readonly Func<Task> _sessionGone;
    private readonly Action _refused;
    private readonly Action _held;
    private readonly Action<DepotNoteSaveState, int?> _state;
    private readonly TimeProvider _time;
    private readonly TimeSpan _quiet;
    private readonly IReadOnlyList<TimeSpan> _retry;
    private readonly ILogger? _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _life = new();

    private bool _active;
    private string? _session;
    private string? _base;
    private bool _owe;
    private bool _isHeld;
    private Task? _busy;
    private bool _again;
    private long _changes;
    private int? _version;
    private int _failures;
    private ITimer? _timer;
    private DateTimeOffset _firstOwed;
    private bool _destroyed;

    public DepotNoteSaver(
        Func<CancellationToken, Task<string>> read, DepotNotePost post, Func<Task> sessionGone, Action refused, Action held,
        Action<DepotNoteSaveState, int?> state, TimeProvider? time = null, TimeSpan? quiet = null, IReadOnlyList<TimeSpan>? retry = null,
        ILogger? logger = null)
    {
        _read = read;
        _post = post;
        _sessionGone = sessionGone;
        _refused = refused;
        _held = held;
        _state = state;
        _time = time ?? TimeProvider.System;
        _quiet = quiet ?? Quiet;
        _retry = retry ?? Retry;
        _logger = logger;
    }

    public string? Base
    {
        get
        {
            lock (_gate)
            {
                return _base;
            }
        }
    }

    public bool Owed
    {
        get
        {
            lock (_gate)
            {
                return _owe;
            }
        }
    }

    public bool IsHeld
    {
        get
        {
            lock (_gate)
            {
                return _isHeld;
            }
        }
    }

    // Something in the doc changed, a peer's edit or a merge: when it is quiet, a save is due if one is owed.
    public void Changed()
    {
        lock (_gate)
        {
            _changes++;
            _Arm();
        }
    }

    // A person typed, here or elsewhere in the room: the note is ahead of the file. Seeding and opening never call this.
    public void Edited()
    {
        lock (_gate)
        {
            if (!_owe)
            {
                _owe = true;
                _firstOwed = _time.GetUtcNow();
            }

            _changes++;
            _Arm();
        }
    }

    public void SetSaver(bool active)
    {
        lock (_gate)
        {
            _active = active;
            if (active)
            {
                _Arm();
            }
            else
            {
                _StopTimer();
            }
        }
    }

    public void SetSession(string? session)
    {
        lock (_gate)
        {
            _session = session;
            _Arm();
        }
    }

    // The base a Join or a handover names. Unsaved typing on a file that is no longer the one it was written against is a writer
    // outside the room: hold, and merge first.
    public void SetBase(string checksum)
    {
        bool held;
        lock (_gate)
        {
            if (_busy is not null)
            {
                return;
            }

            held = _base is not null && _base != checksum && _owe && _active;
            if (held)
            {
                _isHeld = true;
            }
            else
            {
                _base = checksum;
                _Arm();
            }
        }

        if (held)
        {
            _state(DepotNoteSaveState.Held, null);
            _held();
        }
    }

    public void Hold()
    {
        bool owe;
        lock (_gate)
        {
            _isHeld = true;
            _StopTimer();
            owe = _owe;
        }

        if (owe)
        {
            _state(DepotNoteSaveState.Held, null);
        }
    }

    public void Rebase(string checksum)
    {
        lock (_gate)
        {
            _base = checksum;
            _isHeld = false;
            _Arm();
        }
    }

    // Settles when no save is on its way, so a merge reads the base that save leaves behind.
    public async Task IdleAsync()
    {
        Task? busy;
        lock (_gate)
        {
            busy = _busy;
        }

        if (busy is not null)
        {
            await busy.ConfigureAwait(false);
        }
    }

    // Save now if one is owed, for the end of the session; a save already on its way did not carry what was typed since.
    public async Task FlushAsync()
    {
        await _SaveAsync().ConfigureAwait(false);
        bool again;
        lock (_gate)
        {
            again = _owe && _Ready();
        }

        if (again)
        {
            await _SaveAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _destroyed = true;
            _StopTimer();
        }

        _life.Cancel();
    }

    // DEP-513 (saver.ts `says`): a note that says nothing, a bare list marker or &nbsp;, is never written over the file.
    internal static bool Says(string text) => Letter().IsMatch(Entity().Replace(text, string.Empty));

    internal static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // Under _gate.
    private bool _Ready() => _active && !_isHeld && _session is not null && _base is not null && !_destroyed;

    // Under _gate.
    private void _Arm()
    {
        if (!_owe || !_Ready())
        {
            return;
        }

        var waited = _time.GetUtcNow() - _firstOwed;
        _Schedule(TimeSpan.FromTicks(Math.Max(0, Math.Min(_quiet.Ticks, (MaxWait - waited).Ticks))));
    }

    // Under _gate.
    private void _Schedule(TimeSpan delay)
    {
        _StopTimer();
        _timer = _time.CreateTimer(_ => _ = _SaveGuardedAsync(), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void _StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private async Task _SaveGuardedAsync()
    {
        try
        {
            await _SaveAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Saving the live note failed.");
        }
    }

    private Task _SaveAsync()
    {
        lock (_gate)
        {
            _StopTimer();
            if (_busy is { } busy)
            {
                _again = true;
                return busy;
            }

            if (!_owe || !_Ready())
            {
                return Task.CompletedTask;
            }

            _busy = _RunAsync();
            return _busy;
        }
    }

    private async Task _RunAsync()
    {
        // Off the caller's lock: the post and the read run outside it, and the finally below takes it again.
        await Task.Yield();
        try
        {
            await _SaveOnceAsync(_life.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Saving the live note failed; trying again.");
            _state(DepotNoteSaveState.Failed, null);
            lock (_gate)
            {
                _Schedule(_retry[Math.Min(_failures++, _retry.Count - 1)]);
            }
        }
        finally
        {
            lock (_gate)
            {
                _busy = null;
                if (_again)
                {
                    _again = false;
                    _Arm();
                }
            }
        }
    }

    private async Task _SaveOnceAsync(CancellationToken cancellationToken)
    {
        long seen;
        string baseChecksum;
        string session;
        lock (_gate)
        {
            seen = _changes;
            baseChecksum = _base ?? string.Empty;
            session = _session ?? string.Empty;
        }

        var content = await _read(cancellationToken).ConfigureAwait(false);
        if (!Says(content))
        {
            return;
        }

        // An account change during the read turned the saver off: the post would go out under the new sign-in.
        lock (_gate)
        {
            if (!_Ready())
            {
                return;
            }
        }

        _state(DepotNoteSaveState.Saving, null);
        DepotNoteSaveAnswer answer;
        try
        {
            answer = await _post(content, baseChecksum, session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger?.LogWarning(exception, "Posting the live note to Depot failed.");
            answer = new DepotNoteSaveAnswer(0);
        }

        switch (answer)
        {
            case { Status: 200, Checksum: { } saved }:
                int? version;
                lock (_gate)
                {
                    _base = saved;
                    _version = answer.Version ?? _version;
                    version = _version;
                    _failures = 0;
                    // Typed during the post: that part is still owed.
                    _owe = _changes != seen;
                    _firstOwed = _time.GetUtcNow();
                    _Arm();
                }

                _state(DepotNoteSaveState.Saved, version);
                return;
            case { Status: 409, Checksum: { } current } when current == Sha256(content):
                // The save had landed and its answer got lost.
                lock (_gate)
                {
                    _base = current;
                    _owe = _changes != seen;
                    version = _version;
                    _Arm();
                }

                _state(DepotNoteSaveState.Saved, version);
                return;
            case { Status: 409, Own: true, Checksum: { } current }:
                // A save of this session landed first (a closing flush, the saver before this one): this text already holds it.
                lock (_gate)
                {
                    _base = current;
                    _Arm();
                }

                return;
            case { Status: 409 }:
                lock (_gate)
                {
                    _isHeld = true;
                }

                _state(DepotNoteSaveState.Held, null);
                _held();
                return;
            case { Status: 410 }:
                await _sessionGone().ConfigureAwait(false);
                lock (_gate)
                {
                    _Arm();
                }

                return;
            case { Status: 403 }:
                _refused();
                return;
            case { Status: 422 }:
                // Depot's own check of the same rule as Says: not stored, still owed until the note says something.
                return;
        }

        _state(DepotNoteSaveState.Failed, null);
        lock (_gate)
        {
            _Schedule(_retry[Math.Min(_failures++, _retry.Count - 1)]);
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}\p{So}]")]
    private static partial Regex Letter();

    [GeneratedRegex("&(#x?[0-9a-f]+|[a-z]+);", RegexOptions.IgnoreCase)]
    private static partial Regex Entity();
}
