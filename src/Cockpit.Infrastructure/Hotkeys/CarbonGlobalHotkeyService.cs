using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Hotkeys;

namespace Cockpit.Infrastructure.Hotkeys;

// AC-492: global hotkeys on macOS through Carbon's `RegisterEventHotKey` — needs no Accessibility permission,
// unlike SharpHook's event tap (kCGEventTapOptionDefault), which AC-1314 shows can silently go stale.
// UNVERIFIED on a real Mac as of 2026-09-11: the ticket says what a green build does and does not prove.
[SupportedOSPlatform("macos")]
internal sealed class CarbonGlobalHotkeyService : IGlobalHotkeyService
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    // Four-character codes from CarbonEvents.h: 'keyb', '----', 'hkid'.
    private const uint EventClassKeyboard = 0x6B657962;
    private const uint EventParamDirectObject = 0x2D2D2D2D;
    private const uint TypeEventHotKeyId = 0x686B6964;
    private const uint EventHotKeyPressed = 5;
    private const uint EventHotKeyReleased = 6;

    // 'CKPT' — what our hot-key ids are stamped with, so a handler on the same target can tell ours apart.
    private const uint Signature = 0x434B5054;

    // kVK_F1..kVK_F12 from HIToolbox's Events.h; hardware-bound, so they do not move between macOS versions.
    // ponytail: only the function keys the four defaults sit on (F7–F10) and their neighbours; add letters if a
    // setting ever asks for one.
    private static readonly IReadOnlyDictionary<string, uint> VirtualKeys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    {
        ["F1"] = 0x7A, ["F2"] = 0x78, ["F3"] = 0x63, ["F4"] = 0x76, ["F5"] = 0x60, ["F6"] = 0x61,
        ["F7"] = 0x62, ["F8"] = 0x64, ["F9"] = 0x65, ["F10"] = 0x6D, ["F11"] = 0x67, ["F12"] = 0x6F,
    };

    private readonly ILogger<CarbonGlobalHotkeyService> _logger;

    // Kept as a field so the unmanaged side's pointer to it stays valid for the life of the handler.
    private readonly EventHandlerCallback _callback;
    private readonly HashSet<string> _held = [];
    private readonly Lock _heldGate = new();

    private IntPtr _handler;
    private readonly List<(IntPtr Ref, GlobalHotkeyBinding Binding)> _registered = [];
    private IReadOnlyDictionary<string, string> _triggerDescriptions = new Dictionary<string, string>();

    public CarbonGlobalHotkeyService(ILogger<CarbonGlobalHotkeyService> logger)
    {
        _logger = logger;
        _callback = _OnHotKeyEvent;
    }

    public event EventHandler<string>? Pressed;
    public event EventHandler<string>? Released;
    public event EventHandler? TriggerDescriptionsChanged;

    // Null for a key macOS refused (or one with no virtual-key mapping) — never the configured name, so the
    // settings screen says "refused" rather than showing a key that does not fire.
    public string? TriggerDescriptionFor(string hotkeyId) => _triggerDescriptions.GetValueOrDefault(hotkeyId);

    public async Task StartAsync(IReadOnlyList<GlobalHotkeyBinding> bindings, CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);

        if (bindings.Count == 0)
        {
            return;
        }

        var target = GetApplicationEventTarget();
        EventTypeSpec[] kinds =
        [
            new(EventClassKeyboard, EventHotKeyPressed),
            new(EventClassKeyboard, EventHotKeyReleased),
        ];
        var installed = InstallEventHandler(target, Marshal.GetFunctionPointerForDelegate(_callback), (nuint)kinds.Length, kinds, IntPtr.Zero, out _handler);
        if (installed != 0)
        {
            _logger.LogError("Carbon refused the hot-key event handler (OSStatus {Status}); no global hotkey will fire.", installed);
            return;
        }

        var descriptions = new Dictionary<string, string>();
        foreach (var binding in bindings)
        {
            if (!VirtualKeys.TryGetValue(binding.KeyName, out var virtualKey))
            {
                _logger.LogWarning("Hotkey '{KeyName}' for {HotkeyId} has no macOS virtual-key mapping; it will not fire.", binding.KeyName, binding.Id);
                continue;
            }

            // The id is the 1-based slot in _registered; the handler maps it back.
            var hotKeyId = new EventHotKeyId(Signature, (uint)_registered.Count + 1);
            var status = RegisterEventHotKey(virtualKey, 0, hotKeyId, target, 0, out var reference);
            if (status != 0)
            {
                // Sequoia is known to refuse some modifier-less combinations (Apple forum 763878); said out loud
                // per key, which is what lets Options show it.
                _logger.LogWarning("macOS refused hotkey '{KeyName}' for {HotkeyId} (OSStatus {Status}); it will not fire.", binding.KeyName, binding.Id, status);
                continue;
            }

            _registered.Add((reference, binding));
            descriptions[binding.Id] = binding.KeyName;
        }

        _SetTriggerDescriptions(descriptions);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (reference, _) in _registered)
        {
            UnregisterEventHotKey(reference);
        }

        _registered.Clear();

        if (_handler != IntPtr.Zero)
        {
            RemoveEventHandler(_handler);
            _handler = IntPtr.Zero;
        }

        lock (_heldGate)
        {
            _held.Clear();
        }

        _SetTriggerDescriptions(new Dictionary<string, string>());
        return Task.CompletedTask;
    }

    private void _SetTriggerDescriptions(IReadOnlyDictionary<string, string> descriptions)
    {
        var unchanged = descriptions.Count == _triggerDescriptions.Count
            && descriptions.All(entry => _triggerDescriptions.TryGetValue(entry.Key, out var existing) && existing == entry.Value);

        if (unchanged)
        {
            return;
        }

        _triggerDescriptions = descriptions;
        TriggerDescriptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // Runs on the main thread, where Carbon dispatches. Collapses auto-repeat to one edge, like the other two
    // services; the subscriber is called outside the lock.
    private int _OnHotKeyEvent(IntPtr nextHandler, IntPtr eventRef, IntPtr userData)
    {
        var status = GetEventParameter(eventRef, EventParamDirectObject, TypeEventHotKeyId, IntPtr.Zero, (nuint)Marshal.SizeOf<EventHotKeyId>(), IntPtr.Zero, out EventHotKeyId hotKeyId);
        if (status != 0 || hotKeyId.Signature != Signature || hotKeyId.Id == 0 || hotKeyId.Id > _registered.Count)
        {
            return EventNotHandledErr;
        }

        var binding = _registered[(int)hotKeyId.Id - 1].Binding;
        var pressed = GetEventKind(eventRef) == EventHotKeyPressed;

        bool edge;
        lock (_heldGate)
        {
            edge = pressed ? _held.Add(binding.Id) : _held.Remove(binding.Id);
        }

        if (edge)
        {
            (pressed ? Pressed : Released)?.Invoke(this, binding.Id);
        }

        return 0;
    }

    private const int EventNotHandledErr = -9874;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EventHandlerCallback(IntPtr nextHandler, IntPtr eventRef, IntPtr userData);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EventTypeSpec(uint EventClass, uint EventKind);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EventHotKeyId(uint Signature, uint Id);

    [DllImport(Carbon)]
    private static extern IntPtr GetApplicationEventTarget();

    [DllImport(Carbon)]
    private static extern int InstallEventHandler(IntPtr target, IntPtr handler, nuint numTypes, EventTypeSpec[] types, IntPtr userData, out IntPtr handlerRef);

    [DllImport(Carbon)]
    private static extern int RemoveEventHandler(IntPtr handlerRef);

    [DllImport(Carbon)]
    private static extern int RegisterEventHotKey(uint hotKeyCode, uint modifiers, EventHotKeyId hotKeyId, IntPtr target, uint options, out IntPtr hotKeyRef);

    [DllImport(Carbon)]
    private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    [DllImport(Carbon)]
    private static extern int GetEventParameter(IntPtr eventRef, uint name, uint desiredType, IntPtr actualType, nuint bufferSize, IntPtr actualSize, out EventHotKeyId data);

    [DllImport(Carbon)]
    private static extern uint GetEventKind(IntPtr eventRef);
}
