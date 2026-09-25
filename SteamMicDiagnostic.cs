using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;
using System.Diagnostics;

namespace VRSoundboard;

public sealed record MicPathResult(string CaptureEndpoint, string Format, bool ToneDetected, double PeakToneLevel, double PeakSampleLevel, double CaptureRmsLevel, double OnsetDelayMs, double HighGainToneLevel, double LowGainToneLevel, double GainChangeRatio, int ClippedSampleCount, int GapCount, int ActiveWindows, string Summary);

public sealed class SteamMicDiagnostic(AudioExecutionContext context, AudioDeviceService devices, AudioEngine engine, Storage storage)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<MicPathResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("The microphone path check is already running.");
        MMDevice? device = null;
        WasapiCapture? capture = null;
        var samples = new MemoryStream();
        var sampleGate = new object();
        var accepting = true;
        long playStartedTicks = 0;
        long onsetTicks = 0;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WaveFormat? format = null;
        string endpoint = "";
        try
        {
            context.Invoke(() =>
            {
                device = devices.SteamCapture() ?? throw new InvalidOperationException("Microphone (Steam Streaming Microphone) capture endpoint was not found.");
                endpoint = device.FriendlyName;
                capture = new WasapiCapture(device);
                format = capture.WaveFormat;
                capture.DataAvailable += (_, data) =>
                {
                    if (Volatile.Read(ref playStartedTicks) != 0 && Interlocked.Read(ref onsetTicks) == 0 && format is not null && ContainsTestTone(data.Buffer, data.BytesRecorded, format))
                        Interlocked.CompareExchange(ref onsetTicks, Stopwatch.GetTimestamp(), 0);
                    lock (sampleGate) if (accepting) samples.Write(data.Buffer, 0, data.BytesRecorded);
                };
                capture.RecordingStopped += (_, _) => stopped.TrySetResult();
                capture.StartRecording();
            });
            await Task.Delay(250, cancellationToken);
            var path = MakeTone();
            // Use the normal diagnostic level; AudioEngine applies the calibrated
            // Steam-mic trim before the virtual endpoint so capture remains unclipped.
            var testSoundId = Guid.NewGuid();
            Interlocked.Exchange(ref playStartedTicks, Stopwatch.GetTimestamp());
            engine.Play(new Sound { Id = testSoundId, Name = "Steam mic path check", StoredFilename = Path.GetFileName(path), SourceDurationSeconds = 1.2, OutputGain = .75f, Mode = "retrigger" }, .8f, false);
            await Task.Delay(500, cancellationToken);
            engine.SetSoundOutputGain(testSoundId, .25f);
            await Task.Delay(500, cancellationToken);
            engine.Stop();
            context.Invoke(() => capture?.StopRecording());
            await Task.WhenAny(stopped.Task, Task.Delay(500, CancellationToken.None));
            byte[] bytes;
            lock (sampleGate) bytes = samples.ToArray();
            var onsetMs = Interlocked.Read(ref onsetTicks) == 0 ? -1 : (Interlocked.Read(ref onsetTicks) - Interlocked.Read(ref playStartedTicks)) * 1000d / Stopwatch.Frequency;
            var result = Analyze(endpoint, format!, bytes, onsetMs);
            Log.Information("Steam microphone path check: {Summary}; peak {Peak}; gaps {Gaps}", result.Summary, result.PeakToneLevel, result.GapCount);
            return result;
        }
        finally
        {
            context.Invoke(() =>
            {
                try { capture?.StopRecording(); } catch { }
                capture?.Dispose(); device?.Dispose();
            });
            lock (sampleGate) { accepting = false; samples.Dispose(); }
            _gate.Release();
        }
    }

    private string MakeTone()
    {
        // Use the same 44.1 kHz stereo -> 48 kHz endpoint path as most imported sounds.
        const string filename = "mic-path-test-44100.wav";
        var path = Path.Combine(storage.CachePath, filename);
        if (File.Exists(path)) return path;
        using var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 2));
        for (var i = 0; i < 52920; i++)
        {
            var envelope = Math.Min(1, i / 2205d) * Math.Min(1, (52920 - i) / 2205d);
            var sample = (short)(Math.Sin(2 * Math.PI * 997 * i / 44100) * 8000 * envelope);
            for (var channel = 0; channel < 2; channel++) { writer.WriteByte((byte)sample); writer.WriteByte((byte)(sample >> 8)); }
        }
        return path;
    }

    private static bool ContainsTestTone(byte[] bytes, int byteCount, WaveFormat format)
    {
        var frameSize = format.BlockAlign;
        var frames = Math.Min(byteCount / frameSize, format.SampleRate / 100);
        if (frames == 0) return false;
        double real = 0, imaginary = 0;
        for (var i = 0; i < frames; i++)
        {
            var sample = ReadSample(bytes, i * frameSize, format);
            var angle = 2 * Math.PI * 997 * i / format.SampleRate;
            real += sample * Math.Cos(angle);
            imaginary += sample * Math.Sin(angle);
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / frames >= .02;
    }

    private static MicPathResult Analyze(string endpoint, WaveFormat format, byte[] bytes, double onsetDelayMs)
    {
        var frameSize = format.BlockAlign;
        var frames = bytes.Length / frameSize;
        double peakSample = 0, sumSquares = 0;
        var sampleCount = 0;
        var clipped = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var sample = ReadSample(bytes, frame * frameSize + channel * format.BitsPerSample / 8, format);
                if (!float.IsFinite(sample)) continue;
                var absolute = Math.Abs(sample);
                peakSample = Math.Max(peakSample, absolute);
                sumSquares += sample * sample;
                sampleCount++;
                if (absolute >= .98) clipped++;
            }
        }
        var windowFrames = Math.Max(1, format.SampleRate / 20);
        var levels = new List<double>();
        for (var start = 0; start + windowFrames <= frames; start += windowFrames)
        {
            double real = 0, imaginary = 0;
            for (var i = 0; i < windowFrames; i++)
            {
                var sample = ReadSample(bytes, (start + i) * frameSize, format);
                var angle = 2 * Math.PI * 997 * i / format.SampleRate;
                real += sample * Math.Cos(angle); imaginary += sample * Math.Sin(angle);
            }
            levels.Add(2 * Math.Sqrt(real * real + imaginary * imaginary) / windowFrames);
        }
        var peak = levels.Count == 0 ? 0 : levels.Max();
        var highGain = levels.Skip(6).Take(8).DefaultIfEmpty(0).Average();
        var lowGain = levels.Skip(16).Take(7).DefaultIfEmpty(0).Average();
        var gainRatio = lowGain <= 0 ? 0 : highGain / lowGain;
        var threshold = Math.Max(.004, peak * .25);
        var first = levels.FindIndex(x => x >= threshold);
        var last = levels.FindLastIndex(x => x >= threshold);
        var active = first < 0 ? 0 : last - first + 1;
        var gaps = first < 0 ? 0 : levels.Skip(first).Take(active).Count(x => x < threshold);
        var detected = peak >= .004 && active >= 4;
        var summary = !detected
            ? "Test tone was not clearly detected on the Steam microphone capture endpoint. Steam may not be forwarding render audio into this mic."
            : clipped > 0
                ? $"Test tone reached the Steam microphone, but {clipped} captured samples clipped. Lower the Steam microphone level."
            : gaps == 0
                ? "Test tone reached the Steam microphone continuously in this check."
                : $"Test tone reached the Steam microphone, but {gaps} of {active} capture windows were weak or missing.";
        var rms = sampleCount == 0 ? 0 : Math.Sqrt(sumSquares / sampleCount);
        return new(endpoint, format.ToString(), detected, Math.Round(peak, 4), Math.Round(peakSample, 4), Math.Round(rms, 4), Math.Round(onsetDelayMs, 1), Math.Round(highGain, 4), Math.Round(lowGain, 4), Math.Round(gainRatio, 2), clipped, gaps, active, summary);
    }

    private static float ReadSample(byte[] bytes, int offset, WaveFormat format)
    {
        var subtype = format is WaveFormatExtensible extensible ? extensible.SubFormat : Guid.Empty;
        var floating = format.Encoding == WaveFormatEncoding.IeeeFloat || subtype == new Guid("00000003-0000-0010-8000-00aa00389b71");
        if (floating && format.BitsPerSample == 32) return BitConverter.ToSingle(bytes, offset);
        if (format.BitsPerSample == 16) return BitConverter.ToInt16(bytes, offset) / 32768f;
        if (format.BitsPerSample == 24)
        {
            var value = bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16;
            if ((value & 0x800000) != 0) value |= unchecked((int)0xff000000);
            return value / 8388608f;
        }
        if (format.BitsPerSample == 32) return BitConverter.ToInt32(bytes, offset) / 2147483648f;
        throw new NotSupportedException($"Cannot analyze capture format: {format}");
    }
}
