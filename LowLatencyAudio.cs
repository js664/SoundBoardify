using System.Buffers;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VRSoundboard;

internal static class LowLatencyAudio
{
    public static AudioPipeline Create(AudioFileReader reader, double seconds, WaveFormat targetFormat, float gain)
    {
        ISampleProvider? samples = null;
        GainSampleProvider? gainStage = null;
        try
        {
            samples = new LimitedSampleProvider(reader, seconds);
            if (samples.WaveFormat.SampleRate != targetFormat.SampleRate)
                samples = new WdlResamplingSampleProvider(samples, targetFormat.SampleRate);
            var callbackFrames = Math.Max(1, (int)Math.Ceiling(targetFormat.SampleRate * 0.02));
            if (samples.WaveFormat.Channels != targetFormat.Channels)
                samples = new ChannelMappingSampleProvider(samples, targetFormat.Channels, callbackFrames);
            gainStage = new GainSampleProvider(samples, gain);
            return new AudioPipeline(new NativeFormatWaveProvider(gainStage, targetFormat, callbackFrames), gainStage);
        }
        catch
        {
            if (gainStage is not null) gainStage.Dispose();
            else if (samples is IDisposable disposable) disposable.Dispose();
            reader.Dispose();
            throw;
        }
    }
}

internal sealed record AudioPipeline(IWaveProvider WaveProvider, GainSampleProvider GainStage);

internal sealed class GainSampleProvider(ISampleProvider source, float initialGain) : ISampleProvider, IDisposable
{
    private float _gain = Math.Clamp(initialGain, 0, 1);
    public WaveFormat WaveFormat => source.WaveFormat;
    public float Gain { get => Volatile.Read(ref _gain); set => Volatile.Write(ref _gain, Math.Clamp(value, 0, 1)); }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        var gain = Gain;
        if (gain == 1) return read;
        for (var i = 0; i < read; i++) buffer[offset + i] *= gain;
        return read;
    }
    public void Dispose() { if (source is IDisposable disposable) disposable.Dispose(); }
}

internal sealed class LimitedSampleProvider(ISampleProvider source, double seconds) : ISampleProvider
{
    private long _samplesLeft = ((long)Math.Max(0, seconds * source.WaveFormat.SampleRate) * source.WaveFormat.Channels);
    public WaveFormat WaveFormat => source.WaveFormat;
    public int Read(float[] buffer, int offset, int count)
    {
        var allowed = (int)Math.Min(_samplesLeft, count);
        allowed -= allowed % WaveFormat.Channels;
        if (allowed <= 0) return 0;
        var read = source.Read(buffer, offset, allowed);
        _samplesLeft -= read;
        return read;
    }
}

internal sealed class ChannelMappingSampleProvider : ISampleProvider, IDisposable
{
    private readonly ISampleProvider _source;
    private float[] _input;
    public WaveFormat WaveFormat { get; }
    public ChannelMappingSampleProvider(ISampleProvider source, int channels, int preallocatedFrames = 1024)
    {
        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);
        _input = ArrayPool<float>.Shared.Rent(Math.Max(1, preallocatedFrames * source.WaveFormat.Channels));
    }
    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / WaveFormat.Channels;
        if (frames == 0) return 0;
        var sourceChannels = _source.WaveFormat.Channels;
        var needed = frames * sourceChannels;
        if (_input.Length < needed)
        {
            var expanded = ArrayPool<float>.Shared.Rent(needed);
            ArrayPool<float>.Shared.Return(_input);
            _input = expanded;
        }
        var read = _source.Read(_input, 0, needed);
        var readFrames = read / sourceChannels;
        for (var frame = 0; frame < readFrames; frame++)
        {
            var inputOffset = frame * sourceChannels;
            var outputOffset = offset + frame * WaveFormat.Channels;
            if (WaveFormat.Channels == 1)
            {
                float sum = 0;
                for (var channel = 0; channel < sourceChannels; channel++) sum += _input[inputOffset + channel];
                buffer[outputOffset] = sum / sourceChannels;
            }
            else if (sourceChannels == 1)
            {
                for (var channel = 0; channel < WaveFormat.Channels; channel++)
                    buffer[outputOffset + channel] = channel < 2 ? _input[inputOffset] : 0;
            }
            else
            {
                for (var channel = 0; channel < WaveFormat.Channels; channel++)
                    buffer[outputOffset + channel] = channel < sourceChannels ? _input[inputOffset + channel] : 0;
            }
        }
        return readFrames * WaveFormat.Channels;
    }
    public void Dispose()
    {
        var input = _input;
        _input = [];
        if (input.Length > 0) ArrayPool<float>.Shared.Return(input);
    }
}

internal sealed class NativeFormatWaveProvider : IWaveProvider, IDisposable
{
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");
    private readonly ISampleProvider _source;
    private readonly bool _float;
    private readonly int _bytesPerSample;
    private float[] _samples;
    public WaveFormat WaveFormat { get; }

    public NativeFormatWaveProvider(ISampleProvider source, WaveFormat format, int preallocatedFrames = 1024)
    {
        _source = source;
        WaveFormat = format;
        if (source.WaveFormat.SampleRate != format.SampleRate || source.WaveFormat.Channels != format.Channels)
            throw new ArgumentException("The sample stream does not match the endpoint mix format.");
        var subtype = format is WaveFormatExtensible extensible ? extensible.SubFormat : Guid.Empty;
        _float = format.Encoding == WaveFormatEncoding.IeeeFloat || subtype == FloatSubtype;
        var pcm = format.Encoding == WaveFormatEncoding.Pcm || subtype == PcmSubtype;
        if (!_float && !pcm) throw new NotSupportedException($"Unsupported endpoint sample format: {format}");
        if (_float && format.BitsPerSample != 32 || pcm && format.BitsPerSample is not (16 or 24 or 32))
            throw new NotSupportedException($"Unsupported endpoint bit depth: {format}");
        _bytesPerSample = format.BitsPerSample / 8;
        _samples = ArrayPool<float>.Shared.Rent(Math.Max(1, preallocatedFrames * format.Channels));
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var sampleCount = count / WaveFormat.BlockAlign * WaveFormat.Channels;
        if (sampleCount == 0) return 0;
        if (_samples.Length < sampleCount)
        {
            var expanded = ArrayPool<float>.Shared.Rent(sampleCount);
            ArrayPool<float>.Shared.Return(_samples);
            _samples = expanded;
        }
        var read = _source.Read(_samples, 0, sampleCount);
        if (_float)
        {
            for (var i = 0; i < read; i++) _samples[i] = Math.Clamp(_samples[i], -1, 1);
            Buffer.BlockCopy(_samples, 0, buffer, offset, read * _bytesPerSample);
            return read * _bytesPerSample;
        }
        for (var i = 0; i < read; i++)
        {
            var sample = Math.Clamp(_samples[i], -1, 1);
            var positiveMax = (1L << (WaveFormat.BitsPerSample - 1)) - 1;
            var negativeMax = 1L << (WaveFormat.BitsPerSample - 1);
            // Promote before multiplying: converting Int32.MaxValue to float rounds it
            // up to 2^31, which turns +1.0 into the negative full-scale bit pattern.
            var value = (long)Math.Round((double)sample * (sample < 0 ? negativeMax : positiveMax));
            var position = offset + i * _bytesPerSample;
            for (var b = 0; b < _bytesPerSample; b++) buffer[position + b] = (byte)(value >> (b * 8));
        }
        return read * _bytesPerSample;
    }
    public void Dispose()
    {
        var samples = _samples;
        _samples = [];
        if (samples.Length > 0) ArrayPool<float>.Shared.Return(samples);
        if (_source is IDisposable disposable) disposable.Dispose();
    }
}
