using System.Text.Json.Serialization;

namespace VRSoundboard;

public sealed class Sound
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New sound";
    public string SourceFilename { get; set; } = "";
    public string? SourceProvider { get; set; }
    public string? SourceUrl { get; set; }
    public string StoredFilename { get; set; } = "";
    public int SortOrder { get; set; }
    // Kept for backward-compatible imports/edits. Playback now uses OutputGain.
    public float Volume { get; set; } = 1;
    public float OutputGain { get; set; } = 0.75f;
    public double StartSeconds { get; set; }
    public double? EndSeconds { get; set; }
    public double SourceDurationSeconds { get; set; }
    public string Mode { get; set; } = "toggle";
    public string? Hotkey { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? Icon { get; set; }
    public string? ButtonLabel { get; set; }
    public string? ImageFilename { get; set; }
    [JsonIgnore] public double PlayDuration => Math.Max(0, (EndSeconds ?? SourceDurationSeconds) - StartSeconds);
    [JsonIgnore] public string PlaybackStatus { get; set; } = "";

    // Every member is a value type or immutable string, so a shallow snapshot is
    // independent and avoids serializing/deserializing a sound on the play path.
    internal Sound Copy() => (Sound)MemberwiseClone();
}

public static class SoundHotkey
{
    private static readonly System.Text.RegularExpressions.Regex Pattern = new(
        @"^Control\+Alt\+(?<key>[A-Z0-9]|F(?:[1-9]|1[0-2]))$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Pattern.Match(value.Trim());
        if (!match.Success) throw new ArgumentException("Use Ctrl + Alt with a letter, number, or F1–F12.");
        return $"Control+Alt+{match.Groups["key"].Value.ToUpperInvariant()}";
    }
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
    // Optional headroom for virtual microphone endpoints. Kept independent of device names.
    public float MicOutputGain { get; set; } = 0.025f;
    public bool UseVirtualMicHeadroom { get; set; }
    public int OutputHeadroomPreferenceVersion { get; set; }
    public bool MonitorLocally { get; set; } = true;
    public int LocalMonitorPreferenceVersion { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; }
    public string Theme { get; set; } = "system";
    public string? PairingToken { get; set; }
    public long MaxUploadBytes { get; set; } = 30 * 1024 * 1024;

    // Settings contain only values and immutable strings; keep snapshots cheap on
    // playback and update paths without a JSON round trip.
    internal AppSettings Copy() => (AppSettings)MemberwiseClone();
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
    bool UseVirtualMicHeadroom,
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
        settings.UseVirtualMicHeadroom,
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
