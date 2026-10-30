using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;

namespace Cockpit.App.ViewModels;

public sealed partial class SwarmOptionsViewModel(ISwarmOptions? options = null, ISwarmLeadership? leadership = null) : ObservableObject
{
    private SwarmJoinHandshake? _handshake;
    private bool _discovered;
    private bool _refreshing;
    private string? _answeredPairing;
    private DispatcherTimer? _timer;

    public ObservableCollection<SwarmMachineRow> Machines { get; } = [];
    public ObservableCollection<NodeDiscoveryFound> Found { get; } = [];
    public bool NoOtherMachineOnline => !Machines.Any(machine => !machine.IsSelf && machine.IsOnline);
    public IReadOnlyList<string> ReturnChoices { get; } = ["Take over after a wait", "Join at the back"];

    [ObservableProperty] private string _leaderLine = "Leader: this machine · No other machine online";
    [ObservableProperty] private string _rankOrder = "Ranks are assigned when your machines join.";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _extraInternalCidrsText = "";
    [ObservableProperty] private int _returnChoice;
    [ObservableProperty] private string _cidrError = "";
    [ObservableProperty] private string _persistError = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _pairingCode = "";
    [ObservableProperty] private string _pairingCaption = "";
    [ObservableProperty] private SwarmJoinPending? _incoming;

    public void StartListening()
    {
        if (options is null || _timer is not null) return;
        if (leadership is not null) leadership.Changed += _LeadershipChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += _Tick;
        _timer.Start();
        _ = RefreshAsync();
    }

    public void StopListening()
    {
        if (leadership is not null) leadership.Changed -= _LeadershipChanged;
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= _Tick;
            _timer = null;
        }
    }

    private void _LeadershipChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
    private void _Tick(object? sender, EventArgs e) => _ = RefreshAsync();

    public async Task LoadSettingsAsync()
    {
        if (options is null) return;
        try
        {
            var settings = await options.LoadSettingsAsync();
            ExtraInternalCidrsText = string.Join(", ", settings.ExtraInternalCidrs);
            ReturnChoice = settings.WhenALowerRankComesBack == SwarmLowerRankReturn.JoinAtBack ? 1 : 0;
            PersistError = "";
        }
        catch (Exception ex) { PersistError = $"Could not load Swarm settings. {ex.Message}"; }
    }

    partial void OnExtraInternalCidrsTextChanged(string value) => _ValidateCidrs();

    private string[] _ValidateCidrs()
    {
        var cidrs = ExtraInternalCidrsText.Split([',', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var invalid = cidrs.FirstOrDefault(cidr => InternalNetworkRanges.ParseExtra([cidr]).Count != 1);
        CidrError = invalid is null ? "" : $"“{invalid}” is not an internal CIDR range. Use a network such as 100.64.0.0/10; /0 is not allowed.";
        return cidrs;
    }

    public async Task SaveStagedAsync()
    {
        var cidrs = _ValidateCidrs();
        if (CidrError.Length > 0 || options is null) return;
        try
        {
            var settings = await options.LoadSettingsAsync();
            await options.SaveSettingsAsync(settings with
            {
                ExtraInternalCidrs = cidrs,
                WhenALowerRankComesBack = ReturnChoice == 1 ? SwarmLowerRankReturn.JoinAtBack : SwarmLowerRankReturn.AnnouncedTakeover,
            });
            PersistError = "";
        }
        catch (Exception ex) { PersistError = $"Swarm settings were not saved. {ex.Message}"; }
    }

    public void RestoreDefaults()
    {
        ExtraInternalCidrsText = "";
        ReturnChoice = 0;
    }

    public async Task RefreshAsync()
    {
        if (options is null || _refreshing) return;
        _refreshing = true;
        try
        {
            var machines = await options.MachinesAsync();
            var leader = leadership?.Current;
            Machines.Clear();
            foreach (var machine in machines)
            {
                var member = machine.Member;
                Machines.Add(new(member.Name, string.Join(", ", member.Addresses.Select(address =>
                    new UriBuilder("https", address, member.Port).Uri.Authority)),
                    machine.IsSelf ? "This machine" : machine.Origin,
                    member.Rank.ToString(CultureInfo.InvariantCulture),
                    member.Id == leader?.Id ? "Leader" : machine.Presence.Status switch
                    {
                        SwarmPresence.Online => "Online",
                        SwarmPresence.Asleep => "Asleep",
                        _ => "Not reachable",
                    }, machine.IsSelf ? "Now" : machine.Presence.LastSeen?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Not yet seen",
                    machine.IsSelf, machine.Presence.Status == SwarmPresence.Online));
            }
            var reachable = machines.Count(machine => machine.Presence.Status == SwarmPresence.Online);
            LeaderLine = leader is null ? "Leader: this machine · No other machine online"
                : leadership?.PendingTakeover is { } pendingLeader
                    ? $"Leader: {leader.Name} · Lower rank returning: {pendingLeader.Member.Name}"
                    : $"Leader: {leader.Name} · Lowest rank of the {reachable} reachable";
            RankOrder = machines.Count == 0 ? "Ranks are assigned when your machines join."
                : string.Join(" → ", machines.OrderBy(machine => machine.Member.Rank).Select(machine => $"{machine.Member.Name} ({machine.Member.Rank.ToString(CultureInfo.InvariantCulture)})"));
            Incoming = options.Pending is { } pending && pending.PairingId != _answeredPairing ? pending : null;
            OnPropertyChanged(nameof(NoOtherMachineOnline));
        }
        catch (Exception ex) { Status = $"Could not refresh the swarm. {ex.Message}"; }
        finally { _refreshing = false; }
    }

    [RelayCommand]
    private async Task FindAsync()
    {
        if (options is null || IsBusy) return;
        IsBusy = true;
        Status = "Looking for Cockpits on this network…";
        try
        {
            Found.Clear();
            foreach (var machine in await options.FindAsync()) Found.Add(machine);
            Status = Found.Count == 0 ? "No Cockpit found. Enable accepting connections on the other machine, or add its address." : "Choose a machine to compare its pairing code.";
        }
        catch (Exception ex) { Status = $"Discovery failed. {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task PairFoundAsync(NodeDiscoveryFound machine) => _BeginAsync(machine.Address, true);

    [RelayCommand]
    private Task AddByAddressAsync() => _BeginAsync(Address, false);

    private async Task _BeginAsync(string address, bool discovered)
    {
        if (options is null || IsBusy) return;
        IsBusy = true;
        CancelPairing();
        try
        {
            _handshake = await options.BeginAsync(address.Trim());
            _discovered = discovered;
            PairingCaption = $"Compare with {_handshake.Offer.NodeName}. Confirm on that machine too, then continue here.";
            PairingCode = _handshake.Code;
            Status = "Pairing expires after two minutes. Only continue if both screens show the same six digits.";
        }
        catch (Exception ex) { Status = $"Could not start pairing. {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ConfirmPairingAsync()
    {
        if (options is null || _handshake is null || IsBusy) return;
        IsBusy = true;
        try
        {
            await options.CompleteAsync(_handshake, _discovered);
            Status = $"{_handshake.Offer.NodeName} joined the swarm. It appears in Machines and is shared with your other members.";
            CancelPairing();
            await RefreshAsync();
        }
        catch (Exception ex) { Status = $"Pairing not completed. {ex.Message} Confirm on the other machine, then try again."; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void CancelPairing()
    {
        _handshake = null;
        PairingCode = "";
    }

    [RelayCommand]
    private void ConfirmIncoming() => _AnswerIncoming(true);

    [RelayCommand]
    private void RefuseIncoming() => _AnswerIncoming(false);

    private void _AnswerIncoming(bool confirm)
    {
        if (options is null || Incoming is not { } incoming) return;
        try
        {
            if (confirm) options.Confirm(incoming.PairingId);
            else options.Refuse(incoming.PairingId);
            Status = confirm ? "Confirmed here. Continue on the machine adding this one." : "Pairing refused.";
            _answeredPairing = incoming.PairingId;
            Incoming = null;
        }
        catch (Exception ex) { Status = $"Could not answer the pairing. {ex.Message}"; }
    }
}

public sealed record SwarmMachineRow(string Name, string Address, string Origin, string Rank, string Status,
    string LastSeen, bool IsSelf, bool IsOnline)
{
    public bool IsLeader => Status == "Leader";
}
