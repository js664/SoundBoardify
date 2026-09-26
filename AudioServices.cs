using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VRSoundboard;

public sealed class AudioDeviceService(AudioExecutionContext context) : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceEnumerator Enumerator => _enumerator ??= new MMDeviceEnumerator();
    public IReadOnlyList<DeviceInfo> Enumerate(string? selectedId)
    {
        if (!context.IsCurrent) return context.Invoke(() => Enumerate(selectedId));
        var result = new List<DeviceInfo>();
        foreach (var d in Enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All))
        {
            try
            {
                var id = d.ID; var name = d.FriendlyName; var state = d.State.ToString();
                try { var f = d.AudioClient.MixFormat; result.Add(new DeviceInfo(id, name, state, f.ToString(), f.SampleRate, f.Channels, id == selectedId)); }
                catch { result.Add(new DeviceInfo(id, name, state, "Unavailable", 0, 0, id == selectedId)); }
            }
            finally { d.Dispose(); }
        }
        return result;
    }
    public MMDevice? Resolve(string? selectedId)
    {
        if (!context.IsCurrent) return context.Invoke(() => Resolve(selectedId));
        if (string.IsNullOrWhiteSpace(selectedId))
        {
            try { return Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console); }
            catch (Exception ex) { Log.Warning(ex, "Windows default playback endpoint is unavailable"); return null; }
        }
        var devices = Enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var selected = devices.FirstOrDefault(d => d.ID == selectedId);
        foreach (var device in devices) if (!ReferenceEquals(device, selected)) device.Dispose();
        return selected;
    }
    public (bool CurrentEndpointActive, string? DefaultEndpointId) GetReconnectState(string? currentId, bool followsWindowsDefault)
    {
        if (!context.IsCurrent) return context.Invoke(() => GetReconnectState(currentId, followsWindowsDefault));
        MMDevice? defaultDevice = null;
        try
        {
            if (followsWindowsDefault)
            {
                try { defaultDevice = Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console); }
                catch (Exception ex) when (ex is NAudio.MmException or COMException or InvalidOperationException) { }
            }
            var defaultId = defaultDevice?.ID;
            var checkId = currentId ?? defaultId;
            var active = false;
            if (checkId is not null)
            {
                if (defaultDevice is not null && string.Equals(defaultId, checkId, StringComparison.Ordinal))
                {
                    try { active = defaultDevice.State == DeviceState.Active; }
                    catch (Exception ex) when (ex is NAudio.MmException or COMException or InvalidOperationException) { active = false; }
                }
                else
                {
                    try { using var selected = Enumerator.GetDevice(checkId); active = selected.State == DeviceState.Active; }
                    catch (Exception ex) when (ex is NAudio.MmException or COMException or InvalidOperationException or ArgumentException) { active = false; }
                }
            }
            return (active, defaultId);
        }
        finally { defaultDevice?.Dispose(); }
    }
    public MMDevice DefaultRender() => context.IsCurrent ? Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console) : context.Invoke(DefaultRender);
    public MMDevice? SteamCapture()
    {
        if (!context.IsCurrent) return context.Invoke(SteamCapture);
        var devices = Enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        var selected = devices.FirstOrDefault(d => d.FriendlyName.Contains("Steam Streaming Microphone", StringComparison.OrdinalIgnoreCase));
        foreach (var device in devices) if (!ReferenceEquals(device, selected)) device.Dispose();
        return selected;
    }
    public string? DefaultRenderName() => context.Invoke(() =>
    {
        try { using var device = DefaultRender(); return device.FriendlyName; }
        catch (Exception ex) { Log.Warning(ex, "Default Windows playback endpoint is unavailable"); return null; }
    });
    public string? DeviceName(string? endpointId)
    {
        if (!context.IsCurrent) return context.Invoke(() => DeviceName(endpointId));
        try
        {
            using var device = endpointId is null ? DefaultRender() : Enumerator.GetDevice(endpointId);
            return device.FriendlyName;
        }
        catch (Exception ex) when (ex is NAudio.MmException or COMException or InvalidOperationException or ArgumentException)
        {
            Log.Debug(ex, "Could not resolve playback endpoint name {EndpointId}", endpointId);
            return null;
        }
    }
    public void Dispose() => context.Invoke(() => { _enumerator?.Dispose(); _enumerator = null; });
}

public sealed class AudioEngine(AudioDeviceService devices, Storage storage, AudioExecutionContext context) : IDisposable
{
    private readonly object _gate = new();
    private WasapiOut? _output;
    private SwitchingWaveProvider? _source;
    private GainSampleProvider? _gainStage;
    private float _mainGainMultiplier = 1;
    private float _activeSoundOutputGain = 1;
    private MMDevice? _device;
    private string? _endpointId;
    private string? _endpointName;
    private MMDevice? _monitorDevice;
    private WasapiOut? _monitorOutput;
    private SwitchingWaveProvider? _monitorSource;
    private GainSampleProvider? _monitorGainStage;
    private float _monitorGainMultiplier = 1;
    private int _tickPending;
    private string? _monitorEndpointId;
    private string? _monitorEndpointName;
    private Guid? _soundId;
    private string? _selectedId;
    private double _endSeconds;
    private double _startSeconds;
    private readonly Stopwatch _playClock = new();
    private string _status = "Disconnected";
    public event Action<string, object?>? Changed;
    public string Status { get { lock (_gate) return _status; } }
    public string? EndpointName { get { lock (_gate) return _endpointName; } }
    public string? EndpointId { get { lock (_gate) return _endpointId; } }
    public string? MonitorEndpointName { get { lock (_gate) return _monitorEndpointName; } }
    public PlaybackState Playback { get { lock (_gate) return new(_soundId, _soundId is null ? 0 : Math.Min(_endSeconds, _startSeconds + _playClock.Elapsed.TotalSeconds), _soundId is not null); } }

    public void Connect(string? selectedId)
    {
        if (!context.IsCurrent) { context.Invoke(() => Connect(selectedId)); return; }
        Guid? stopped;
        lock (_gate)
        {
            _selectedId = selectedId;
            stopped = _soundId;
            StopLocked(); DisconnectOutputLocked(); _device?.Dispose(); _device = null; _endpointId = null; _endpointName = null;
            try
            {
                _device = devices.Resolve(selectedId);
                if (_device is null) _status = selectedId is null ? "Windows default playback endpoint was not found." : "Selected playback endpoint was not found.";
                else
                {
                    _endpointId = _device.ID; _endpointName = _device.FriendlyName;
                    var mix = _device.AudioClient.MixFormat;
                    _source = new SwitchingWaveProvider(mix);
                    _output = new WasapiOut(_device, AudioClientShareMode.Shared, false, 10);
                    var currentOutput = _output;
                    currentOutput.PlaybackStopped += (_, args) =>
                    {
                        if (args.Exception is null) return;
                        context.Post(() =>
                        {
                            lock (_gate) { if (!ReferenceEquals(currentOutput, _output)) return; StopLocked(); DisconnectOutputLocked(); _status = "Audio device error: " + args.Exception.Message; }
                            Log.Error(args.Exception, "WASAPI playback stopped");
                            Connect(_selectedId);
                        });
                    };
                    currentOutput.Init(_source);
                    currentOutput.Play();
                    _status = "Connected · " + mix;
                }
                Log.Information("Audio endpoint: {Status} {Endpoint}", _status, _device?.FriendlyName);
            }
            catch (Exception ex) { _status = "Audio device error: " + ex.Message; Log.Error(ex, "Audio connection failed"); }
        }
        if (stopped is not null) Changed?.Invoke("sound-stopped", new { id = stopped });
        Changed?.Invoke("audio-device", new { status = Status, endpoint = EndpointName });
    }
    public void Play(Sound sound, float masterVolume, bool monitorLocally = false, float micOutputGain = 0.025f, string? monitorEndpointId = null, bool useVirtualMicHeadroom = false)
    {
        if (!context.IsCurrent) { context.Invoke(() => Play(sound, masterVolume, monitorLocally, micOutputGain, monitorEndpointId, useVirtualMicHeadroom)); return; }
        lock (_gate)
        {
            if (_soundId == sound.Id && sound.Mode == "toggle") { StopLocked(); Changed?.Invoke("sound-stopped", new { id = sound.Id }); return; }
            var previous = _soundId;
            // Keep the WASAPI stream live while preparing a retrigger. The source
            // is replaced atomically below, avoiding a callback-sized silence gap.
            _soundId = null;
            _playClock.Reset();
            if (previous is not null && previous != sound.Id) Changed?.Invoke("sound-stopped", new { id = previous });
            if (_device is null) throw new InvalidOperationException(_status);
            AudioFileReader? preparedReader = null;
            try
            {
                var path = Path.Combine(storage.CachePath, sound.StoredFilename);
                var micGain = CalculateMainOutputGain(useVirtualMicHeadroom, micOutputGain);
                _activeSoundOutputGain = sound.OutputGain;
                preparedReader = new AudioFileReader(path) { Volume = 1 };
                preparedReader.CurrentTime = TimeSpan.FromSeconds(sound.StartSeconds);
                _startSeconds = sound.StartSeconds;
                _endSeconds = sound.EndSeconds ?? sound.SourceDurationSeconds;
                if (_output is null || _source is null) throw new InvalidOperationException(_status);
                var mainPipeline = LowLatencyAudio.Create(preparedReader, _endSeconds - _startSeconds, _device.AudioClient.MixFormat, sound.OutputGain * masterVolume * micGain);
                try { _source.Replace(preparedReader, mainPipeline.WaveProvider); }
                catch
                {
                    if (mainPipeline.WaveProvider is IDisposable disposable) disposable.Dispose();
                    throw;
                }
                preparedReader = null;
                _gainStage = mainPipeline.GainStage;
                _mainGainMultiplier = masterVolume * micGain;
                if (monitorLocally)
                {
                    try
                    {
                        using var monitorTarget = string.IsNullOrWhiteSpace(monitorEndpointId)
                            ? devices.DefaultRender()
                            : devices.Resolve(monitorEndpointId) ?? throw new InvalidOperationException("The selected local monitor device is unavailable.");
                        if (_monitorDevice is null || _monitorEndpointId != monitorTarget.ID)
                        {
                            StopMonitorLocked();
                            if (monitorTarget.ID != _device.ID)
                            {
                                _monitorDevice = devices.Resolve(monitorTarget.ID);
                                if (_monitorDevice is null) throw new InvalidOperationException("The default playback device is unavailable.");
                                _monitorEndpointId = _monitorDevice.ID;
                                _monitorEndpointName = _monitorDevice.FriendlyName;
                                _monitorSource = new SwitchingWaveProvider(_monitorDevice.AudioClient.MixFormat);
                                _monitorOutput = new WasapiOut(_monitorDevice, AudioClientShareMode.Shared, false, 10);
                                _monitorOutput.Init(_monitorSource);
                                _monitorOutput.Play();
                            }
                        }
                        else _monitorEndpointName = _monitorDevice.FriendlyName;
                        if (_monitorSource is not null && _monitorDevice is not null)
                        {
                            AudioFileReader? monitorReader = new AudioFileReader(path) { Volume = 1 };
                            AudioPipeline monitorPipeline;
                            try
                            {
                                monitorReader.CurrentTime = TimeSpan.FromSeconds(sound.StartSeconds);
                                monitorPipeline = LowLatencyAudio.Create(monitorReader, _endSeconds - _startSeconds, _monitorDevice.AudioClient.MixFormat, sound.OutputGain * masterVolume);
                                try { _monitorSource.Replace(monitorReader, monitorPipeline.WaveProvider); }
                                catch
                                {
                                    if (monitorPipeline.WaveProvider is IDisposable disposable) disposable.Dispose();
                                    throw;
                                }
                                monitorReader = null;
                            }
                            finally { monitorReader?.Dispose(); }
                            _monitorGainStage = monitorPipeline.GainStage;
                            _monitorGainMultiplier = masterVolume;
                        }
                    }
                    catch (Exception monitorError) { Log.Warning(monitorError, "Local soundboard monitor failed; microphone injection continues"); StopMonitorLocked(); }
                }
                else { _monitorSource?.Clear(); _monitorGainStage = null; }
                _soundId = sound.Id;
                _playClock.Restart();
                Log.Information("Playing {SoundId} on {Endpoint}", sound.Id, _device.FriendlyName);
                if (_monitorEndpointName is not null) Log.Information("Local soundboard monitor: {Endpoint}", _monitorEndpointName);
            }
            catch (Exception ex)
            {
                preparedReader?.Dispose();
                StopLocked(); _status = "Playback failed: " + ex.Message; Log.Error(ex, "Playback failed");
                Changed?.Invoke("audio-device", new { status = _status, endpoint = EndpointName });
                throw;
            }
        }
        Changed?.Invoke("sound-started", new { id = sound.Id });
    }
    public void Stop()
    {
        if (!context.IsCurrent) { context.Invoke(Stop); return; }
        Guid? old;
        lock (_gate) { old = _soundId; StopLocked(); }
        if (old is not null) Changed?.Invoke("sound-stopped", new { id = old });
    }
    public void SetSoundOutputGain(Guid soundId, float gain)
    {
        if (!context.IsCurrent) { context.Invoke(() => SetSoundOutputGain(soundId, gain)); return; }
        lock (_gate)
        {
            if (_soundId != soundId) return;
            _activeSoundOutputGain = Math.Clamp(gain, 0, 1);
            if (_gainStage is not null) _gainStage.Gain = _activeSoundOutputGain * _mainGainMultiplier;
            if (_monitorGainStage is not null) _monitorGainStage.Gain = _activeSoundOutputGain * _monitorGainMultiplier;
        }
    }
    internal static float CalculateMainOutputGain(bool useVirtualMicHeadroom, float micOutputGain)
        => useVirtualMicHeadroom ? Math.Clamp(micOutputGain, 0, 0.25f) : 1f;

    public void SetMixLevels(float masterVolume, float micOutputGain, bool useVirtualMicHeadroom)
    {
        if (!context.IsCurrent) { context.Invoke(() => SetMixLevels(masterVolume, micOutputGain, useVirtualMicHeadroom)); return; }
        lock (_gate)
        {
            var master = Math.Clamp(masterVolume, 0, 1);
            var mic = CalculateMainOutputGain(useVirtualMicHeadroom, micOutputGain);
            _mainGainMultiplier = master * mic;
            _monitorGainMultiplier = master;
            if (_gainStage is not null) _gainStage.Gain = _activeSoundOutputGain * _mainGainMultiplier;
            if (_monitorGainStage is not null) _monitorGainStage.Gain = _activeSoundOutputGain * _monitorGainMultiplier;
        }
    }
    public void Tick()
    {
        if (!context.IsCurrent)
        {
            if (Interlocked.Exchange(ref _tickPending, 1) != 0) return;
            context.Post(() => { Interlocked.Exchange(ref _tickPending, 0); Tick(); });
            return;
        }
        Guid? completed = null; PlaybackState state;
        lock (_gate)
        {
            if (_soundId is not null && _startSeconds + _playClock.Elapsed.TotalSeconds >= _endSeconds) { completed = _soundId; StopLocked(); }
            state = new(_soundId, _soundId is null ? 0 : Math.Min(_endSeconds, _startSeconds + _playClock.Elapsed.TotalSeconds), _soundId is not null);
        }
        if (completed is not null) Changed?.Invoke("sound-stopped", new { id = completed });
        if (state.Playing) Changed?.Invoke("position", state);
    }
    private void StopLocked()
    {
        _soundId = null;
        _playClock.Reset();
        _source?.Clear();
        _monitorSource?.Clear();
        _gainStage = null;
        _monitorGainStage = null;
    }
    private void StopMonitorLocked()
    {
        _monitorSource?.Clear(); _monitorSource = null; _monitorGainStage = null;
        var output = _monitorOutput; _monitorOutput = null;
        try { output?.Stop(); } catch { }
        output?.Dispose();
        _monitorDevice?.Dispose(); _monitorDevice = null; _monitorEndpointName = null;
        _monitorEndpointId = null;
    }
    private void DisconnectOutputLocked()
    {
        StopMonitorLocked();
        _source?.Clear(); _source = null; _gainStage = null;
        var output = _output; _output = null;
        try { output?.Stop(); } catch { }
        output?.Dispose();
    }
    public void Dispose()
    {
        if (!context.IsCurrent) { context.Invoke(Dispose); return; }
        lock (_gate) { StopLocked(); DisconnectOutputLocked(); _device?.Dispose(); _device = null; }
    }
}

internal sealed class SwitchingWaveProvider(WaveFormat waveFormat) : IWaveProvider
{
    private readonly object _gate = new();
    private AudioFileReader? _reader;
    private IWaveProvider? _current;
    public WaveFormat WaveFormat { get; } = waveFormat;
    public void Replace(AudioFileReader reader, IWaveProvider source)
    {
        AudioFileReader? previousReader;
        IWaveProvider? previousSource;
        lock (_gate)
        {
            previousReader = _reader;
            previousSource = _current;
            _reader = reader;
            _current = source;
        }
        DisposeResources(previousReader, previousSource);
    }
    public void Clear()
    {
        AudioFileReader? reader;
        IWaveProvider? source;
        lock (_gate)
        {
            source = _current;
            reader = _reader;
            _current = null;
            _reader = null;
        }
        DisposeResources(reader, source);
    }
    public int Read(byte[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        AudioFileReader? finishedReader = null;
        IWaveProvider? finishedSource = null;
        int result;
        lock (_gate)
        {
            if (_current is null) return count;
            var current = _current;
            var read = current.Read(buffer, offset, count);
            if (read < count)
            {
                Array.Clear(buffer, offset + read, count - read);
                _current = null;
                finishedReader = _reader;
                finishedSource = current;
                _reader = null;
                result = count;
            }
            else result = read;
        }
        // This method is called on WASAPI's render thread. File close and pooled-buffer
        // cleanup can take longer than an audio period, so retire finished streams away
        // from the callback after atomically detaching them from future reads.
        if ((finishedReader is not null || finishedSource is not null) &&
            !ThreadPool.QueueUserWorkItem(static state =>
            {
                var retired = (RetiredAudio)state!;
                DisposeResources(retired.Reader, retired.Source);
            }, new RetiredAudio(finishedReader, finishedSource), preferLocal: false))
            DisposeResources(finishedReader, finishedSource);
        return result;
    }

    private static void DisposeResources(AudioFileReader? reader, IWaveProvider? source)
    {
        try { if (source is IDisposable disposable) disposable.Dispose(); }
        catch (Exception ex) { Log.Warning(ex, "Could not dispose a completed audio stream"); }
        try { reader?.Dispose(); }
        catch (Exception ex) { Log.Warning(ex, "Could not close a completed audio file"); }
    }

    private sealed record RetiredAudio(AudioFileReader? Reader, IWaveProvider? Source);
}
