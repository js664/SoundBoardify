using System.Text.Json.Serialization;

namespace VRSoundboard;

public sealed class Sound
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New sound";
    public string SourceFilename { get; set; } = "";
    public string StoredFilename { get; set; } = "";
    public int SortOrder { get; set; }
    // Kept for backward-compatible imports/edits. Playback now uses OutputGain.
    public float Volume { get; set; } = 1;
    public float OutputGain { get; set; } = 0.75f;
    public double StartSeconds { get; set; }
    public double? EndSeconds { get; set; }
    public double SourceDurationSeconds { get; set; }
    public string Mode { get; set; } = "toggle";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? Icon { get; set; }
    public string? ButtonLabel { get; set; }
    public string? ImageFilename { get; set; }
    [JsonIgnore] public double PlayDuration => Math.Max(0, (EndSeconds ?? SourceDurationSeconds) - StartSeconds);
    [JsonIgnore] public string PlaybackStatus { get; set; } = "";
}

public sealed class AppSettings
{
    public bool WebEnabled { get; set; } = true;
    public int Port { get; set; } = 6769;
    public bool LanAccess { get; set; } = true;
    public bool TailscaleAccess { get; set; }
    public string? EndpointId { get; set; }
    public string? MonitorEndpointId { get; set; }
    public int ButtonDensity { get; set; } = 9;
    public bool ReconnectAudio { get; set; } = true;
    public float MasterVolume { get; set; } = 1;
    // Headroom compensation for the strong gain in Steam's virtual mic capture path.
    public float MicOutputGain { get; set; } = 0.025f;
    public bool MonitorLocally { get; set; } = true;
    public int LocalMonitorPreferenceVersion { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; }
    public string Theme { get; set; } = "system";
    public string? PairingToken { get; set; }
    public long MaxUploadBytes { get; set; } = 30 * 1024 * 1024;
}

// Settings returned to the web UI deliberately never include the pairing secret.
public sealed record AppSettingsView(
    bool WebEnabled,
    int Port,
    bool LanAccess,
    bool TailscaleAccess,
    string? EndpointId,
    string? MonitorEndpointId,
    int ButtonDensity,
    bool ReconnectAudio,
    float MasterVolume,
    float MicOutputGain,
    bool MonitorLocally,
    bool StartWithWindows,
    bool StartMinimized,
    bool MinimizeToTray,
    string Theme,
    bool PairingEnabled,
    long MaxUploadBytes)
{
    public static AppSettingsView From(AppSettings settings) => new(
        settings.WebEnabled,
        settings.Port,
        settings.LanAccess,
        settings.TailscaleAccess,
        settings.EndpointId,
        settings.MonitorEndpointId,
        settings.ButtonDensity,
        settings.ReconnectAudio,
        settings.MasterVolume,
        settings.MicOutputGain,
        settings.MonitorLocally,
        settings.StartWithWindows,
        settings.StartMinimized,
        settings.MinimizeToTray,
        settings.Theme,
        !string.IsNullOrWhiteSpace(settings.PairingToken),
        settings.MaxUploadBytes);
}

public record DeviceInfo(string Id, string Name, string State, string Format, int SampleRate, int Channels, bool Selected);
public record PlaybackState(Guid? SoundId, double PositionSeconds, bool Playing);
