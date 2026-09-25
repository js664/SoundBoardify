using NAudio.Wave;

namespace VRSoundboard;

public static class TestTone
{
    public static void Play(AudioEngine engine, float volume, Storage? storage = null, bool monitorLocally = true, string? monitorEndpointId = null)
    {
        storage ??= AppServices.Storage ?? throw new InvalidOperationException("Storage is unavailable.");
        const string name = "test-tone.wav";
        var path = Path.Combine(storage.CachePath, name);
        if (!File.Exists(path))
        {
            using var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 1));
            for (var i = 0; i < 9600; i++)
            {
                var envelope = Math.Min(1, i / 700.0) * Math.Min(1, (9600 - i) / 1300.0);
                var sample = (short)(Math.Sin(2 * Math.PI * 660 * i / 48000) * 5000 * envelope);
                writer.WriteByte((byte)sample); writer.WriteByte((byte)(sample >> 8));
            }
        }
        engine.Play(new Sound { Id = Guid.NewGuid(), Name = "Test tone", StoredFilename = name, SourceDurationSeconds = .2, Mode = "retrigger" }, volume, monitorLocally, monitorEndpointId: monitorEndpointId);
    }
}

public static class AppServices { public static Storage? Storage { get; set; } }
