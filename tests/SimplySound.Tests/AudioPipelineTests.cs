using System.Net;
using System.Net.NetworkInformation;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VRSoundboard;
using Xunit;

namespace SimplySound.Tests;

public sealed class AudioPipelineTests
{
    [Fact]
    public void AudioResourceCleanupContainsEndpointDisposalFailures()
    {
        var resource = new ThrowingDisposable();

        AudioResourceCleanup.Dispose(resource, "test endpoint");

        Assert.True(resource.Disposed);
    }

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
    public async Task TrimmingAnAudioClipShorterThanTenMillisecondsKeepsAValidRange()
    {
        var root = Path.Combine(Path.GetTempPath(), "SimplySound-short-sound-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "short.wav");
        try
        {
            Directory.CreateDirectory(root);
            using (var writer = new WaveFileWriter(path, new WaveFormat(8000, 16, 1))) writer.WriteSamples(new short[8], 0, 8);
            var storage = new Storage(root);
            storage.Initialize();
            var library = new SoundLibrary(storage);
            await using var stream = File.OpenRead(path);
            var sound = await library.ImportAsync(stream, "short.wav");

            var updated = library.Update(sound.Id, clip => clip.EndSeconds = sound.SourceDurationSeconds / 2);

            Assert.InRange(updated.EndSeconds!.Value, updated.StartSeconds, sound.SourceDurationSeconds);
            Assert.True(updated.PlayDuration > 0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root) && Path.GetFileName(root).StartsWith("SimplySound-short-sound-test-", StringComparison.Ordinal))
                Directory.Delete(root, recursive: true);
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

    [Theory]
    [InlineData(false, 0.025f, 1f)]
    [InlineData(true, 0.025f, 0.025f)]
    [InlineData(true, 0.5f, 0.25f)]
    [InlineData(true, -1f, 0f)]
    public void VirtualMicrophoneHeadroomIsExplicitAndClamped(bool enabled, float configured, float expected)
        => Assert.Equal(expected, AudioEngine.CalculateMainOutputGain(enabled, configured));

    [Theory]
    [InlineData("Speakers (Steam Streaming Microphone)", true)]
    [InlineData("Virtual Cable Input", false)]
    [InlineData("Speakers", false)]
    [InlineData(null, false)]
    public void LegacyHeadroomMigrationOnlyPreservesTheOldSteamMicSetup(string? endpointName, bool expected)
        => Assert.Equal(expected, AppCoordinator.ShouldPreserveLegacyVirtualMicHeadroom(endpointName));

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
    public void SixChannelDownmixKeepsCenterAndSurroundButOmitsLfe()
    {
        var surround51 = new ChannelMappingSampleProvider(
            new FloatSource(WaveFormat.CreateIeeeFloatWaveFormat(48000, 6), [.1f, .2f, .3f, .95f, .4f, .5f]), 2);
        var stereo = new float[2];

        Assert.Equal(2, surround51.Read(stereo, 0, stereo.Length));
        Assert.Equal(.1f + (.3f + .4f) * .70710678f, stereo[0], 5);
        Assert.Equal(.2f + (.3f + .5f) * .70710678f, stereo[1], 5);
    }

    [Fact]
    public void EightChannelDownmixIncludesBackAndSidePairs()
    {
        var surround71 = new ChannelMappingSampleProvider(
            new FloatSource(WaveFormat.CreateIeeeFloatWaveFormat(48000, 8), [.1f, .2f, .3f, .95f, .4f, .5f, .6f, .7f]), 2);
        var stereo = new float[2];

        Assert.Equal(2, surround71.Read(stereo, 0, stereo.Length));
        Assert.Equal(.1f + (.3f + .4f + .6f) * .70710678f, stereo[0], 5);
        Assert.Equal(.2f + (.3f + .5f + .7f) * .70710678f, stereo[1], 5);
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
        using var output = new NativeFormatWaveProvider(source, new WaveFormat(48000, 16, 1));
        var bytes = new byte[6];
        Assert.Equal(6, output.Read(bytes, 0, bytes.Length));
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(bytes, 0));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(bytes, 2));
        Assert.Equal(16384, BitConverter.ToInt16(bytes, 4));
    }

    [Fact]
    public void NativePcmConversionWritesSignedTwentyFourBitSamplesInLittleEndianOrder()
    {
        var source = new FloatSource(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), [-1f, 0f, 1f]);
        using var output = new NativeFormatWaveProvider(source, new WaveFormat(48000, 24, 1));
        var bytes = new byte[9];

        Assert.Equal(bytes.Length, output.Read(bytes, 0, bytes.Length));
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0, 0, 0, 0xff, 0xff, 0x7f }, bytes);
    }

    [Fact]
    public void NativePcmConversionClampsSignedThirtyTwoBitSamples()
    {
        var source = new FloatSource(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), [-1.5f, 0.5f, 1.5f]);
        using var output = new NativeFormatWaveProvider(source, new WaveFormat(48000, 32, 1));
        var bytes = new byte[12];

        Assert.Equal(bytes.Length, output.Read(bytes, 0, bytes.Length));
        Assert.Equal(int.MinValue, BitConverter.ToInt32(bytes, 0));
        Assert.Equal(1_073_741_824, BitConverter.ToInt32(bytes, 4));
        Assert.Equal(int.MaxValue, BitConverter.ToInt32(bytes, 8));
    }

    [Fact]
    public void FailedPipelineConstructionReleasesTheSourceFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2))) writer.WriteSamples(new short[32], 0, 32);
            var reader = new AudioFileReader(path);

            Assert.Throws<NotSupportedException>(() => LowLatencyAudio.Create(reader, 1, new WaveFormat(48000, 8, 1), 1));

            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void FormatConversionBuffersDoNotAllocateOnTheAudioReadPath()
    {
        var source = new FloatSource(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), new float[100_000]);
        var mapped = new ChannelMappingSampleProvider(source, 6, preallocatedFrames: 256);
        var gain = new GainSampleProvider(mapped, 1);
        using var output = new NativeFormatWaveProvider(gain, new WaveFormat(48000, 16, 6), preallocatedFrames: 256);
        var bytes = new byte[256 * 6 * 2];
        for (var i = 0; i < 3; i++) Assert.Equal(bytes.Length, output.Read(bytes, 0, bytes.Length));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var read = 0;
        for (var i = 0; i < 20; i++) read = output.Read(bytes, 0, bytes.Length);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(bytes.Length, read);
        Assert.Equal(before, after);
    }

    [Fact]
    public void ResamplingDoesNotAllocateOnTheSteadyStateAudioReadPath()
    {
        var source = new FloatSource(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new float[200_000]);
        var resampled = new WdlResamplingSampleProvider(source, 48000);
        using var output = new NativeFormatWaveProvider(new GainSampleProvider(resampled, 1), new WaveFormat(48000, 16, 2), preallocatedFrames: 256);
        var bytes = new byte[256 * 2 * 2];
        for (var i = 0; i < 8; i++) Assert.Equal(bytes.Length, output.Read(bytes, 0, bytes.Length));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var read = 0;
        for (var i = 0; i < 20; i++) read = output.Read(bytes, 0, bytes.Length);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(bytes.Length, read);
        Assert.Equal(before, after);
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
            var firstSource = new ByteSource(format, [1, 2, 3, 4]);
            switching.Replace(new AudioFileReader(path), firstSource);
            var first = new byte[4];
            switching.Read(first, 0, first.Length);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, first);
            var nextSource = new ByteSource(format, [9, 8, 7, 6]);
            switching.Replace(new AudioFileReader(path), nextSource);
            Assert.True(firstSource.Disposed);
            var next = new byte[4];
            switching.Read(next, 0, next.Length);
            Assert.Equal(new byte[] { 9, 8, 7, 6 }, next);
            switching.Clear();
            Assert.True(nextSource.Disposed);
            Assert.Equal(4, switching.Read(next, 0, next.Length));
            Assert.Equal(new byte[4], next);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EndOfStreamDisposalDoesNotBlockRenderReads()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowDisposal = new ManualResetEventSlim();
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(8000, 16, 1))) writer.WriteSamples(new short[8], 0, 8);
            var format = WaveFormat.CreateIeeeFloatWaveFormat(8000, 1);
            var switching = new SwitchingWaveProvider(format);
            switching.Replace(new AudioFileReader(path), new BlockingDisposeByteSource(format, disposalStarted, allowDisposal));

            var output = new byte[4];
            var firstRead = Task.Run(() => switching.Read(output, 0, output.Length));
            Assert.Equal(4, await firstRead.WaitAsync(TimeSpan.FromMilliseconds(500)));
            await disposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // A file close may still be blocked on a worker, but the next WASAPI read
            // must see silence immediately without waiting for that disposal.
            var secondRead = Task.Run(() => switching.Read(output, 0, output.Length));
            Assert.Equal(4, await secondRead.WaitAsync(TimeSpan.FromMilliseconds(500)));
            Assert.Equal(new byte[4], output);
            allowDisposal.Set();
            switching.Clear();
        }
        finally
        {
            allowDisposal.Set();
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (File.Exists(path) && DateTime.UtcNow < deadline)
            {
                try { File.Delete(path); }
                catch (IOException) { await Task.Delay(10); }
            }
        }
    }

    [Fact]
    public async Task RetriggerDisposalDoesNotBlockRenderReads()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowDisposal = new ManualResetEventSlim();
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(8000, 16, 1))) writer.WriteSamples(new short[8], 0, 8);
            var format = WaveFormat.CreateIeeeFloatWaveFormat(8000, 1);
            var switching = new SwitchingWaveProvider(format);
            switching.Replace(new AudioFileReader(path), new BlockingDisposeByteSource(format, disposalStarted, allowDisposal));

            var replacementSource = new ByteSource(format, [9, 8, 7, 6]);
            var replacing = Task.Run(() => switching.Replace(new AudioFileReader(path), replacementSource));
            await disposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // The new stream is already visible before the retired stream finishes
            // disposing, so a WASAPI callback never waits behind old-file cleanup.
            var output = new byte[4];
            var read = Task.Run(() => switching.Read(output, 0, output.Length));
            Assert.Equal(4, await read.WaitAsync(TimeSpan.FromMilliseconds(500)));
            Assert.Equal(new byte[] { 9, 8, 7, 6 }, output);
            allowDisposal.Set();
            await replacing.WaitAsync(TimeSpan.FromSeconds(2));
            switching.Clear();
        }
        finally
        {
            allowDisposal.Set();
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (File.Exists(path) && DateTime.UtcNow < deadline)
            {
                try { File.Delete(path); }
                catch (IOException) { await Task.Delay(10); }
            }
        }
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

    private sealed class ByteSource(WaveFormat waveFormat, byte[] bytes) : IWaveProvider, IDisposable
    {
        private bool _read;
        public bool Disposed { get; private set; }
        public WaveFormat WaveFormat { get; } = waveFormat;
        public int Read(byte[] buffer, int offset, int count)
        {
            if (_read) return 0;
            _read = true;
            var size = Math.Min(count, bytes.Length);
            Array.Copy(bytes, 0, buffer, offset, size);
            return size;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose()
        {
            Disposed = true;
            throw new InvalidOperationException("Endpoint disconnected during disposal.");
        }
    }

    private sealed class BlockingDisposeByteSource(WaveFormat waveFormat, TaskCompletionSource disposalStarted, ManualResetEventSlim allowDisposal) : IWaveProvider, IDisposable
    {
        public WaveFormat WaveFormat { get; } = waveFormat;
        public int Read(byte[] buffer, int offset, int count) => 0;
        public void Dispose()
        {
            disposalStarted.TrySetResult();
            allowDisposal.Wait(TimeSpan.FromSeconds(5));
        }
    }
}

public sealed class RuntimeBehaviorTests
{
    [Fact]
    public void NetworkAddressSelectionPrefersRoutedLanAndKeepsTailscaleSeparate()
    {
        var snapshots = new[]
        {
            new NetworkAddressSnapshot(true, NetworkInterfaceType.Ethernet, false, [IPAddress.Parse("192.168.10.8")]),
            new NetworkAddressSnapshot(true, NetworkInterfaceType.Wireless80211, true, [IPAddress.Parse("192.168.20.9")]),
            new NetworkAddressSnapshot(true, NetworkInterfaceType.Tunnel, false, [IPAddress.Parse("100.88.2.3")]),
            new NetworkAddressSnapshot(true, NetworkInterfaceType.Ethernet, false, [IPAddress.Parse("169.254.1.2"), IPAddress.Parse("8.8.8.8")]),
            new NetworkAddressSnapshot(false, NetworkInterfaceType.Ethernet, true, [IPAddress.Parse("10.0.0.7")]),
        };

        var selected = AppCoordinator.SelectNetworkAddresses(snapshots);

        Assert.Equal(IPAddress.Parse("192.168.20.9"), selected.Lan);
        Assert.Equal(IPAddress.Parse("100.88.2.3"), selected.Tailscale);
    }

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

    [Fact]
    public void RequestLimitsFollowCurrentSoundSettingAndKeepArtworkAtFiveMegabytes()
    {
        const long oneMiB = 1024 * 1024;
        const long fiveMiB = 5 * oneMiB;
        const long thirtyOneMiB = 31 * oneMiB;
        var imagePath = $"/api/sounds/{Guid.NewGuid()}/image";

        Assert.Equal(2 * oneMiB, WebServerService.GetRequestBodyLimit("POST", "/api/sounds", oneMiB));
        Assert.Equal(thirtyOneMiB, WebServerService.GetRequestBodyLimit("POST", "/api/sounds", 30 * oneMiB));
        Assert.Equal(fiveMiB + oneMiB, WebServerService.GetRequestBodyLimit("POST", imagePath, oneMiB));
        Assert.Equal(fiveMiB + oneMiB, WebServerService.GetRequestBodyLimit("POST", imagePath, 30 * oneMiB));
        Assert.Equal(2 * oneMiB, WebServerService.GetRequestBodyLimit("GET", imagePath, oneMiB));
    }

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

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", true)]
    [InlineData("100.88.2.3", "100.88.2.3", true)]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("localhost", "127.0.0.1", true)]
    [InlineData("attacker.example", "192.168.1.20", false)]
    [InlineData("192.168.1.21", "192.168.1.20", false)]
    public void HostMustResolveToTheInterfaceReceivingTheRequest(string host, string localAddress, bool expected)
        => Assert.Equal(expected, WebServerService.IsRequestHostAllowed(host, IPAddress.Parse(localAddress)));

    [Theory]
    [InlineData("http://192.168.1.20:6769", "http", "192.168.1.20:6769", true)]
    [InlineData("http://127.0.0.1:6769", "http", "127.0.0.1:6769", true)]
    [InlineData("https://192.168.1.20:6769", "http", "192.168.1.20:6769", false)]
    [InlineData("http://attacker.example:6769", "http", "192.168.1.20:6769", false)]
    [InlineData("http://192.168.1.20:6669", "http", "192.168.1.20:6769", false)]
    public void BrowserOriginMustMatchTheRequestedHostAndPort(string origin, string scheme, string host, bool expected)
        => Assert.Equal(expected, WebServerService.IsSameOrigin(origin, scheme, host));

    [Fact]
    public void NonBrowserClientsMayOmitOrigin()
        => Assert.True(WebServerService.IsSameOrigin(null, "http", "127.0.0.1:6769"));

    [Fact]
    public void PublicSettingsExposePairingStateButNeverTheSecret()
    {
        var settings = new AppSettings { PairingToken = "private-pairing-secret" };
        var json = System.Text.Json.JsonSerializer.Serialize(AppSettingsView.From(settings), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"pairingEnabled\":true", json);
        Assert.DoesNotContain("private-pairing-secret", json);
        Assert.DoesNotContain("pairingToken", json);
    }

    [Fact]
    public void PublicSettingsExposeVirtualMicHeadroomControl()
    {
        var settings = new AppSettings { UseVirtualMicHeadroom = true };
        var json = JsonSerializer.Serialize(AppSettingsView.From(settings), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"useVirtualMicHeadroom\":true", json);
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
