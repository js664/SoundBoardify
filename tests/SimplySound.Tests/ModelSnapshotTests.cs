using VRSoundboard;
using Xunit;
using System.Text.Json;

namespace SimplySound.Tests;

public sealed class ModelSnapshotTests
{
    [Fact]
    public void SoundCopyPreservesValuesAndDoesNotShareMutableProperties()
    {
        var sound = new Sound
        {
            Name = "notification",
            SourceFilename = "notification.mp3",
            StoredFilename = "cached.wav",
            SourceProvider = "MyInstants",
            SourceUrl = "https://example.test/sound",
            SortOrder = 7,
            OutputGain = .42f,
            StartSeconds = .5,
            EndSeconds = 1.5,
            SourceDurationSeconds = 2,
            Mode = "retrigger",
            Hotkey = "Control+Alt+N",
            Icon = "music",
            ButtonLabel = "Notify",
            ImageFilename = "cover.png",
            PlaybackStatus = "Playing",
        };

        var copy = sound.Copy();

        Assert.Equal(sound.Id, copy.Id);
        Assert.Equal(sound.Name, copy.Name);
        Assert.Equal(sound.SourceFilename, copy.SourceFilename);
        Assert.Equal(sound.StoredFilename, copy.StoredFilename);
        Assert.Equal(sound.SourceProvider, copy.SourceProvider);
        Assert.Equal(sound.SourceUrl, copy.SourceUrl);
        Assert.Equal(sound.SortOrder, copy.SortOrder);
        Assert.Equal(sound.OutputGain, copy.OutputGain);
        Assert.Equal(sound.StartSeconds, copy.StartSeconds);
        Assert.Equal(sound.EndSeconds, copy.EndSeconds);
        Assert.Equal(sound.SourceDurationSeconds, copy.SourceDurationSeconds);
        Assert.Equal(sound.Mode, copy.Mode);
        Assert.Equal(sound.Hotkey, copy.Hotkey);
        Assert.Equal(sound.Icon, copy.Icon);
        Assert.Equal(sound.ButtonLabel, copy.ButtonLabel);
        Assert.Equal(sound.ImageFilename, copy.ImageFilename);
        Assert.Equal(sound.PlaybackStatus, copy.PlaybackStatus);

        copy.Name = "edited copy";
        copy.PlaybackStatus = "Stopped";
        Assert.Equal("notification", sound.Name);
        Assert.Equal("Playing", sound.PlaybackStatus);
    }

    [Fact]
    public void SettingsCopyPreservesValuesAndCanBeEditedIndependently()
    {
        var settings = new AppSettings
        {
            Port = 6669,
            LanAccess = false,
            TailscaleAccess = true,
            EndpointId = "endpoint-id",
            MonitorEndpointId = "monitor-id",
            ButtonDensity = 15,
            ReconnectAudio = false,
            MasterVolume = .65f,
            MicOutputGain = .12f,
            UseVirtualMicHeadroom = true,
            MonitorLocally = false,
            StartWithWindows = true,
            StartMinimized = true,
            MinimizeToTray = true,
            Theme = "dark",
            PairingToken = "private-token",
            MaxUploadBytes = 12_345_678,
        };

        var copy = settings.Copy();

        Assert.Equal(settings.Port, copy.Port);
        Assert.Equal(settings.LanAccess, copy.LanAccess);
        Assert.Equal(settings.TailscaleAccess, copy.TailscaleAccess);
        Assert.Equal(settings.EndpointId, copy.EndpointId);
        Assert.Equal(settings.MonitorEndpointId, copy.MonitorEndpointId);
        Assert.Equal(settings.ButtonDensity, copy.ButtonDensity);
        Assert.Equal(settings.ReconnectAudio, copy.ReconnectAudio);
        Assert.Equal(settings.MasterVolume, copy.MasterVolume);
        Assert.Equal(settings.MicOutputGain, copy.MicOutputGain);
        Assert.Equal(settings.UseVirtualMicHeadroom, copy.UseVirtualMicHeadroom);
        Assert.Equal(settings.MonitorLocally, copy.MonitorLocally);
        Assert.Equal(settings.StartWithWindows, copy.StartWithWindows);
        Assert.Equal(settings.StartMinimized, copy.StartMinimized);
        Assert.Equal(settings.MinimizeToTray, copy.MinimizeToTray);
        Assert.Equal(settings.Theme, copy.Theme);
        Assert.Equal(settings.PairingToken, copy.PairingToken);
        Assert.Equal(settings.MaxUploadBytes, copy.MaxUploadBytes);

        copy.PairingToken = "rotated-copy";
        copy.Port = 7000;
        Assert.Equal("private-token", settings.PairingToken);
        Assert.Equal(6669, settings.Port);
    }

    [Fact]
    public void PlaybackSnapshotsAllocateLessThanJsonRoundTrips()
    {
        var sound = new Sound { Name = "Short alert", SourceFilename = "alert.mp3", StoredFilename = "alert.wav" };
        var settings = new AppSettings { PairingToken = "private-token", EndpointId = "endpoint-id" };

        var soundCopyBytes = MeasureAllocations(sound, value => value.Copy());
        var soundJsonBytes = MeasureAllocations(sound, value => JsonSerializer.Deserialize<Sound>(JsonSerializer.Serialize(value))!);
        var settingsCopyBytes = MeasureAllocations(settings, value => value.Copy());
        var settingsJsonBytes = MeasureAllocations(settings, value => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(value))!);

        Assert.True(soundCopyBytes * 4 < soundJsonBytes, $"Expected sound snapshots to allocate much less ({soundCopyBytes} vs {soundJsonBytes} bytes).");
        Assert.True(settingsCopyBytes * 4 < settingsJsonBytes, $"Expected settings snapshots to allocate much less ({settingsCopyBytes} vs {settingsJsonBytes} bytes).");
    }

    private static long MeasureAllocations<T>(T value, Func<T, T> copy)
    {
        _ = copy(value); // Warm the serializer or copy method before measuring.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        object? last = null;
        for (var i = 0; i < 256; i++) last = copy(value);
        GC.KeepAlive(last);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
