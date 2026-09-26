using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;

namespace VRSoundboard;

public sealed class AppCoordinator(Storage storage, SoundLibrary library, AudioEngine audio, AudioDeviceService devices)
{
    private const int PlaybackUpdateIntervalMs = 50;
    private readonly object _settingsGate = new();
    private readonly object _networkGate = new();
    private AppSettings _settings = storage.LoadSettings();
    private int _activeWebPort;
    private long _networkCacheExpiresAt;
    private string _lanAddress = "127.0.0.1";
    private string? _tailscaleAddress;
    private bool _networkEventsAttached;
    private readonly System.Threading.Timer _timer = new(_ => audio.Tick(), null, Timeout.Infinite, Timeout.Infinite);
    private System.Threading.Timer? _reconnectTimer;
    public event Action<string, object?>? Changed;
    public SoundLibrary Library => library;
    public AudioEngine Audio => audio;
    public AudioDeviceService Devices => devices;
    public Storage Storage => storage;
    public AppSettings Settings { get { lock (_settingsGate) return Clone(_settings); } }
    public string LanAddress => CurrentNetworkAddresses.Lan;
    public string? TailscaleAddress => CurrentNetworkAddresses.Tailscale;
    private (string Lan, string? Tailscale) CurrentNetworkAddresses
    {
        get
        {
            lock (_networkGate)
            {
                if (Environment.TickCount64 >= _networkCacheExpiresAt) RefreshNetworkAddressesLocked();
                return (_lanAddress, _tailscaleAddress);
            }
        }
    }
    private void RefreshNetworkAddressesLocked()
    {
        try
        {
            var snapshots = new List<NetworkAddressSnapshot>();
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                var isUp = network.OperationalStatus == OperationalStatus.Up;
                if (!isUp) continue;
                try
                {
                    var properties = network.GetIPProperties();
                    var hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address));
                    snapshots.Add(new NetworkAddressSnapshot(isUp, network.NetworkInterfaceType, hasGateway, properties.UnicastAddresses.Select(a => a.Address).ToArray()));
                }
                catch (NetworkInformationException) { }
            }
            var selected = SelectNetworkAddresses(snapshots);
            _lanAddress = selected.Lan?.ToString() ?? "127.0.0.1";
            _tailscaleAddress = selected.Tailscale?.ToString();
        }
        catch (NetworkInformationException ex) { Log.Warning(ex, "Could not refresh local network addresses; keeping the last known addresses"); }
        finally { _networkCacheExpiresAt = Environment.TickCount64 + 5000; }
    }
    private void InvalidateNetworkAddresses()
    {
        lock (_networkGate) _networkCacheExpiresAt = 0;
    }
    private void OnNetworkAddressChanged(object? sender, EventArgs e) => InvalidateNetworkAddresses();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => InvalidateNetworkAddresses();
    internal static (IPAddress? Lan, IPAddress? Tailscale) SelectNetworkAddresses(IEnumerable<NetworkAddressSnapshot> interfaces)
    {
        var active = interfaces.Where(network => network.IsUp).ToArray();
        var tailscale = active.SelectMany(network => network.Addresses).FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && IsTailscale(address));
        var lan = active
            .Where(network => network.Type != NetworkInterfaceType.Loopback)
            .SelectMany(network =>
            {
                var interfaceScore = network.Type switch
                {
                    NetworkInterfaceType.Wireless80211 => 20,
                    NetworkInterfaceType.Ethernet => 15,
                    _ => 0
                };
                return network.Addresses
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(address) && !IsTailscale(address) && !IsLinkLocal(address))
                    .Select(address => (Address: address, Score: interfaceScore + (network.HasIpv4Gateway ? 100 : 0)));
            })
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Address)
            .FirstOrDefault();
        return (lan, tailscale);
    }
    public int ActiveWebPort => Volatile.Read(ref _activeWebPort);
    public int EffectivePort => ActiveWebPort is > 0 ? ActiveWebPort : Settings.Port;
    public string? PhoneUrl => Settings.LanAccess && LanAddress != "127.0.0.1" ? BuildPhoneUrl(LanAddress, EffectivePort, Settings.PairingToken) : null;
    public string? TailscalePhoneUrl => Settings.TailscaleAccess && TailscaleAddress is { } address ? BuildPhoneUrl(address, EffectivePort, Settings.PairingToken) : null;
    public static string BuildPhoneUrl(string address, int port, string? token) => $"http://{address}:{port}" + (string.IsNullOrWhiteSpace(token) ? "" : "?token=" + Uri.EscapeDataString(token));
    public void SetActiveWebPort(int? port) => Volatile.Write(ref _activeWebPort, port ?? 0);
    public AppCoordinator Initialize()
    {
        if (!_networkEventsAttached)
        {
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            _networkEventsAttached = true;
        }
        // Existing installations stored the old opt-in default as false. Apply the new
        // user-requested default once, then preserve later changes to the checkbox.
        if (_settings.LocalMonitorPreferenceVersion == 0)
        {
            _settings.MonitorLocally = true;
            _settings.LocalMonitorPreferenceVersion = 1;
            storage.Save(_settings);
        }
        if (_settings.OutputHeadroomPreferenceVersion == 0)
        {
            _settings.UseVirtualMicHeadroom = ShouldPreserveLegacyVirtualMicHeadroom(devices.DeviceName(_settings.EndpointId));
            _settings.OutputHeadroomPreferenceVersion = 1;
            storage.Save(_settings);
        }
        library.Changed += (type, value) => Changed?.Invoke(type, value);
        audio.Changed += (type, value) => Changed?.Invoke(type, value);
        audio.Connect(_settings.EndpointId);
        _timer.Change(PlaybackUpdateIntervalMs, PlaybackUpdateIntervalMs);
        _reconnectTimer = new System.Threading.Timer(_ => CheckAudio(), null, 5000, 5000);
        Log.Information("Application started; web dashboard is available on port {Port}", EffectivePort);
        return this;
    }
    public static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        var b = address.GetAddressBytes();
        return b.Length == 4 && (IsTailscale(address) || b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254);
    }
    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
    public static bool IsTailscale(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }
    public static IPAddress? NormalizeNetworkAddress(IPAddress? address)
        => address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;
    public static bool CanAccessFromNetwork(IPAddress? address, bool lanAccess, bool tailscaleAccess)
    {
        address = NormalizeNetworkAddress(address);
        if (address is null) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (IsTailscale(address)) return tailscaleAccess;
        return lanAccess && IsPrivate(address);
    }
    internal static bool ShouldPreserveLegacyVirtualMicHeadroom(string? endpointName)
        => endpointName?.Contains("Steam Streaming Microphone", StringComparison.OrdinalIgnoreCase) == true;
    public AppSettingsView SettingsView() => AppSettingsView.From(Settings);
    private static AppSettings Clone(AppSettings s) => s.Copy();
    public AppSettings UpdateSettings(Action<AppSettings> change)
    {
        AppSettings result;
        lock (_settingsGate)
        {
            var candidate = Clone(_settings);
            change(candidate);
            candidate.MasterVolume = Math.Clamp(candidate.MasterVolume, 0, 1);
            candidate.MicOutputGain = Math.Clamp(candidate.MicOutputGain, 0, 0.25f);
            if (candidate.Port is < 1024 or > 65535) throw new ArgumentException("Port must be between 1024 and 65535.");
            if (candidate.ButtonDensity is not (6 or 9 or 12 or 15 or 20)) throw new ArgumentException("Invalid button density.");
            if (candidate.MaxUploadBytes is < 1024 * 1024 or > 200L * 1024 * 1024) throw new ArgumentException("Upload limit must be between 1 and 200 MB.");
            storage.Save(candidate); _settings = candidate; result = Clone(candidate);
        }
        audio.SetMixLevels(result.MasterVolume, result.MicOutputGain, result.UseVirtualMicHeadroom);
        Changed?.Invoke("settings-changed", AppSettingsView.From(result));
        return result;
    }
    public void Play(Guid id)
    {
        var sound = library.Get(id) ?? throw new KeyNotFoundException("Sound not found.");
        var settings = Settings;
        audio.Play(sound, settings.MasterVolume, settings.MonitorLocally, settings.MicOutputGain, settings.MonitorEndpointId, settings.UseVirtualMicHeadroom);
    }
    public Sound UpdateSound(Guid id, Action<Sound> change)
    {
        var sound = library.Update(id, change);
        audio.SetSoundOutputGain(id, sound.OutputGain);
        return sound;
    }
    public void Stop() => audio.Stop();
    public void SelectDevice(string? endpointId)
    {
        UpdateSettings(s => s.EndpointId = endpointId);
        audio.Connect(endpointId);
    }
    public void SelectMonitorDevice(string? endpointId) => UpdateSettings(s => s.MonitorEndpointId = endpointId);
    private void CheckAudio()
    {
        if (!Settings.ReconnectAudio) return;
        try
        {
            var settings = Settings;
            var id = audio.EndpointId;
            var reconnectState = devices.GetReconnectState(id, settings.EndpointId is null);
            if (ShouldReconnectAudio(settings.EndpointId is null, id, reconnectState.DefaultEndpointId, reconnectState.CurrentEndpointActive, audio.Status.StartsWith("Connected", StringComparison.Ordinal)))
            { audio.Connect(settings.EndpointId); return; }
        }
        catch (Exception ex) { Log.Warning(ex, "Audio reconnect check failed"); }
    }
    public static bool ShouldReconnectAudio(bool followsWindowsDefault, string? currentEndpointId, string? windowsDefaultId, bool currentEndpointActive, bool connected)
        => !currentEndpointActive || !connected || followsWindowsDefault && !string.Equals(currentEndpointId, windowsDefaultId, StringComparison.Ordinal);
    public void Shutdown()
    {
        if (_networkEventsAttached)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            _networkEventsAttached = false;
        }
        _timer.Dispose(); _reconnectTimer?.Dispose(); audio.Dispose();
    }
}

internal sealed record NetworkAddressSnapshot(bool IsUp, NetworkInterfaceType Type, bool HasIpv4Gateway, IReadOnlyList<IPAddress> Addresses);
