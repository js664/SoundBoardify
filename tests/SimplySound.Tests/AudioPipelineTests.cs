using System.Net;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NAudio.Wave;
using VRSoundboard;
using Xunit;

namespace SimplySound.Tests;

public sealed class AudioPipelineTests
{
    [Theory]
    [InlineData("Control+Alt+k", "Control+Alt+K")]
    [InlineData("Control+Alt+7", "Control+Alt+7")]
    [InlineData("Control+Alt+F12", "Control+Alt+F12")]
    public void SoundHotkeysNormalizeSupportedCombinations(string value, string expected)
    {
        Assert.Equal(expected, SoundHotkey.Normalize(value));
    }

    [Theory]
    [InlineData("K")]
    [InlineData("Control+K")]
    [InlineData("Control+Alt+F13")]
    [InlineData("Control+Alt+Escape")]
    public void SoundHotkeysRejectUnmodifiedOrUnsupportedKeys(string value)
    {
        Assert.Throws<ArgumentException>(() => SoundHotkey.Normalize(value));
    }

    [Fact]
    public async Task SoundLibraryPreventsAssigningOneHotkeyToMultipleSounds()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "SimplySound-hotkey-test-" + Guid.NewGuid().ToString("N"));
        var firstPath = Path.Combine(localAppData, "first.wav");
        var secondPath = Path.Combine(localAppData, "second.wav");
        try
        {
            Directory.CreateDirectory(localAppData);
            foreach (var path in new[] { firstPath, secondPath })
            {
                using var writer = new WaveFileWriter(path, new WaveFormat(8000, 16, 1));
                writer.WriteSamples(new short[80], 0, 80);
            }
            var storage = new Storage(localAppData);
            storage.Initialize();
            var library = new SoundLibrary(storage);
            Sound first;
            Sound second;
            await using (var stream = File.OpenRead(firstPath)) first = await library.ImportAsync(stream, "first.wav");
            await using (var stream = File.OpenRead(secondPath)) second = await library.ImportAsync(stream, "second.wav");
            library.Update(first.Id, sound => sound.Hotkey = "Control+Alt+A");

            var error = Assert.Throws<ArgumentException>(() => library.Update(second.Id, sound => sound.Hotkey = "Control+Alt+a"));
            Assert.Contains("already assigned", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(localAppData) && Path.GetFileName(localAppData).StartsWith("SimplySound-hotkey-test-", StringComparison.Ordinal))
                Directory.Delete(localAppData, recursive: true);
        }
    }

    [Fact]
    public void GainChangesAreAppliedToSamplesAsTheyAreRead()
    {
        var source = new FloatSource(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), [1f, -0.5f, 0.25f]);
        var gain = new GainSampleProvider(source, 0.75f);
        var buffer = new float[3];
        Assert.Equal(3, gain.Read(buffer, 0, 3));
        Assert.Equal([0.75f, -0.375f, 0.1875f], buffer);
        gain.Gain = 0.2f;
        source.Reset([1f, -1f]);
        Assert.Equal(2, gain.Read(buffer, 0, 2));
        Assert.Equal([0.2f, -0.2f], buffer[..2]);
    }

    [Fact]
    public void ChannelMappingDownmixesAndDuplicatesMonoWithoutChangingFrameCount()
    {
        var stereo = new ChannelMappingSampleProvider(new FloatSource(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), [1f, -1f, .5f, .25f]), 1);
        var mono = new float[2];
        Assert.Equal(2, stereo.Read(mono, 0, 2));
        Assert.Equal([0f, .375f], mono);
        var duplicated = new ChannelMappingSampleProvider(new FloatSource(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), [.4f, -.2f]), 2);
        var channels = new float[4];
        Assert.Equal(4, duplicated.Read(channels, 0, 4));
        Assert.Equal([.4f, .4f, -.2f, -.2f], channels);
    }

    [Fact]
    public void TrimProviderStopsAtWholeFrames()
    {
        var limited = new LimitedSampleProvider(new FloatSource(WaveFormat.CreateIeeeFloatWaveFormat(10, 2), [1, 2, 3, 4, 5, 6]), .2);
        var buffer = new float[8];
        Assert.Equal(4, limited.Read(buffer, 0, 8));
        Assert.Equal(0, limited.Read(buffer, 0, 8));
    }

    [Fact]
    public void NativePcmConversionClampsAndUsesSignedSixteenBitSamples()
    {
        var source = new FloatSource(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), [1.5f, -1.5f, .5f]);
        var output = new NativeFormatWaveProvider(source, new WaveFormat(48000, 16, 1));
        var bytes = new byte[6];
        Assert.Equal(6, output.Read(bytes, 0, bytes.Length));
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(bytes, 0));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(bytes, 2));
        Assert.Equal(16384, BitConverter.ToInt16(bytes, 4));
    }

    [Fact]
    public void RetriggerProviderAtomicallySwitchesToTheNewStream()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(8000, 16, 1))) writer.WriteSamples(new short[8], 0, 8);
            var format = WaveFormat.CreateIeeeFloatWaveFormat(8000, 1);
            var switching = new SwitchingWaveProvider(format);
            switching.Replace(new AudioFileReader(path), new ByteSource(format, [1, 2, 3, 4]));
            var first = new byte[4];
            switching.Read(first, 0, first.Length);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, first);
            switching.Replace(new AudioFileReader(path), new ByteSource(format, [9, 8, 7, 6]));
            var next = new byte[4];
            switching.Read(next, 0, next.Length);
            Assert.Equal(new byte[] { 9, 8, 7, 6 }, next);
            switching.Clear();
            Assert.Equal(4, switching.Read(next, 0, next.Length));
            Assert.Equal(new byte[4], next);
        }
        finally { File.Delete(path); }
    }

    private sealed class FloatSource(WaveFormat waveFormat, float[] samples) : ISampleProvider
    {
        private float[] _samples = samples;
        private int _position;
        public WaveFormat WaveFormat { get; } = waveFormat;
        public void Reset(float[] values) { _samples = values; _position = 0; }
        public int Read(float[] buffer, int offset, int count)
        {
            var length = Math.Min(count, _samples.Length - _position);
            Array.Copy(_samples, _position, buffer, offset, length);
            _position += length;
            return length;
        }
    }

    private sealed class ByteSource(WaveFormat waveFormat, byte[] bytes) : IWaveProvider
    {
        private bool _read;
        public WaveFormat WaveFormat { get; } = waveFormat;
        public int Read(byte[] buffer, int offset, int count)
        {
            if (_read) return 0;
            _read = true;
            var size = Math.Min(count, bytes.Length);
            Array.Copy(bytes, 0, buffer, offset, size);
            return size;
        }
    }
}

public sealed class RuntimeBehaviorTests
{
    [Theory]
    [InlineData(true, "old", "new", true, true, true)]
    [InlineData(false, "chosen", "windows", true, true, false)]
    [InlineData(false, "chosen", "windows", false, true, true)]
    [InlineData(false, "chosen", "windows", true, false, true)]
    public void ReconnectDecisionTracksDefaultOnlyWhenNoOverride(bool followsDefault, string current, string systemDefault, bool active, bool connected, bool expected)
        => Assert.Equal(expected, AppCoordinator.ShouldReconnectAudio(followsDefault, current, systemDefault, active, connected));

    [Theory]
    [InlineData(6769, 6669)]
    [InlineData(6669, 6769)]
    [InlineData(54321, 6669)]
    public void BusyWebPortUsesTheExpectedFallback(int preferred, int expected)
        => Assert.Equal(expected, WebServerService.FallbackPort(preferred));

    [Theory]
    [InlineData(null, 6769, 6669, false)]
    [InlineData(6769, 6769, 6769, false)]
    [InlineData(6669, 6769, 6669, true)]
    [InlineData(6769, 6769, 6669, true)]
    public void PortCanBeRetriedWhenTheCurrentListenerIsOnFallback(int? requested, int previousSetting, int activePort, bool expected)
        => Assert.Equal(expected, WebServerService.ShouldRestartForPortChange(requested, previousSetting, activePort));

    [Fact]
    public void PhoneUrlCarriesAnEscapedPairingToken()
        => Assert.Equal("http://100.88.2.3:6769?token=a%2Bb%2F%3D", AppCoordinator.BuildPhoneUrl("100.88.2.3", 6769, "a+b/="));

    [Fact]
    public void PairingRejectsRemoteRequestsWithoutTokenButAllowsLoopbackAndValidToken()
    {
        var phone = IPAddress.Parse("192.168.1.20");
        Assert.False(WebServerService.IsPairingAuthorized(phone, "secret", null, null));
        Assert.True(WebServerService.IsPairingAuthorized(phone, "secret", "secret", null));
        Assert.True(WebServerService.IsPairingAuthorized(phone, "secret", null, "secret"));
        Assert.True(WebServerService.IsPairingAuthorized(IPAddress.Loopback, "secret", null, null));
        Assert.True(WebServerService.IsPairingAuthorized(phone, null, null, null));
    }

    [Theory]
    [InlineData("192.168.1.20", true, false, true)]
    [InlineData("192.168.1.20", false, true, false)]
    [InlineData("100.88.2.3", true, false, false)]
    [InlineData("100.88.2.3", false, true, true)]
    [InlineData("8.8.8.8", true, true, false)]
    [InlineData("::ffff:192.168.1.20", true, false, true)]
    [InlineData("::ffff:100.88.2.3", true, false, false)]
    [InlineData("::ffff:100.88.2.3", false, true, true)]
    public void NetworkAccessSeparatesLanFromOptionalTailscale(string remote, bool lanEnabled, bool tailscaleEnabled, bool expected)
        => Assert.Equal(expected, AppCoordinator.CanAccessFromNetwork(IPAddress.Parse(remote), lanEnabled, tailscaleEnabled));

    [Fact]
    public void PairingTreatsMappedLoopbackAsLocal()
        => Assert.True(WebServerService.IsPairingAuthorized(IPAddress.Parse("::ffff:127.0.0.1"), "secret", null, null));

    [Fact]
    public void PublicSettingsExposePairingStateButNeverTheSecret()
    {
        var settings = new AppSettings { PairingToken = "private-pairing-secret" };
        var json = System.Text.Json.JsonSerializer.Serialize(AppSettingsView.From(settings), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"pairingEnabled\":true", json);
        Assert.DoesNotContain("private-pairing-secret", json);
        Assert.DoesNotContain("pairingToken", json);
    }

    [Theory]
    [InlineData("Soundboardify")]
    [InlineData("VRSoundboard")]
    public void LegacyLibraryMigratesIntoAnAlreadyCreatedEmptyAppFolder(string legacyFolderName)
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "SimplySound-storage-test-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(localAppData, legacyFolderName);
        var current = Path.Combine(localAppData, "SimplySound");
        try
        {
            var legacyDatabase = Path.Combine(legacy, "database");
            var emptyDatabase = Path.Combine(current, "database");
            foreach (var path in new[] { legacyDatabase, Path.Combine(legacy, "sounds"), Path.Combine(legacy, "cache"), Path.Combine(legacy, "images"), Path.Combine(legacy, "logs"), emptyDatabase, Path.Combine(current, "sounds"), Path.Combine(current, "cache"), Path.Combine(current, "images"), Path.Combine(current, "logs"), Path.Combine(current, "backend") }) Directory.CreateDirectory(path);
            var id = Guid.NewGuid();
            using (var db = new SqliteConnection($"Data Source={Path.Combine(legacyDatabase, "library.db")};Pooling=False"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "CREATE TABLE sounds(id TEXT PRIMARY KEY, sort_order INTEGER NOT NULL, json TEXT NOT NULL); CREATE TABLE settings(key TEXT PRIMARY KEY, json TEXT NOT NULL); INSERT INTO sounds(id, sort_order, json) VALUES($id, 0, $json); INSERT INTO settings(key, json) VALUES('app', $settings);";
                cmd.Parameters.AddWithValue("$id", id.ToString());
                cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new Sound { Id = id, Name = "Preserved clip", StoredFilename = "preserved.wav" }));
                cmd.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(new AppSettings { PairingToken = "preserved-legacy-token" }));
                cmd.ExecuteNonQuery();
            }
            using (var db = new SqliteConnection($"Data Source={Path.Combine(emptyDatabase, "library.db")};Pooling=False"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "CREATE TABLE sounds(id TEXT PRIMARY KEY, sort_order INTEGER NOT NULL, json TEXT NOT NULL); CREATE TABLE settings(key TEXT PRIMARY KEY, json TEXT NOT NULL); INSERT INTO settings(key, json) VALUES('app', $settings);";
                cmd.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(new AppSettings { LocalMonitorPreferenceVersion = 1 }));
                cmd.ExecuteNonQuery();
            }
            File.WriteAllText(Path.Combine(current, "backend", "preserve-runtime.txt"), "runtime");

            var storage = new Storage(localAppData);
            storage.Initialize();

            Assert.Equal(current, storage.Root);
            Assert.Equal("Preserved clip", Assert.Single(storage.LoadSounds()).Name);
            Assert.Equal("preserved-legacy-token", storage.LoadSettings().PairingToken);
            Assert.True(File.Exists(Path.Combine(current, "backend", "preserve-runtime.txt")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(localAppData) && Path.GetFileName(localAppData).StartsWith("SimplySound-storage-test-", StringComparison.Ordinal))
                Directory.Delete(localAppData, recursive: true);
        }
    }
}
