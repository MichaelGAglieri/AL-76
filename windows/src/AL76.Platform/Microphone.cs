using AL76.Core.Diagnostics;
using AL76.Core.Voice;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// WasapiCapture is marked obsolete in NAudio 3 in favour of WasapiRecorder; it still works and is the documented path.
#pragma warning disable CS0618

namespace AL76.Platform;

/// <summary>
/// The default microphone through WASAPI (shared mode, whatever format Windows gives, usually 48 kHz float
/// stereo), turned into 16 kHz mono and cut into phrases by the VAD. Each phrase arrives on a pool thread.
/// </summary>
public sealed class Microphone : IDisposable
{
    private readonly VoiceActivityDetector vad = new();
    private WasapiCapture? capture;
    private WasapiRecorder? recorder;
    // The last few seconds at 16 kHz, kept even while Listening is false: "basta" is heard while AL-76 speaks.
    private readonly float[] ring = new float[VoiceActivityDetector.SampleRate * 3];
    private int ringEnd;
    private readonly object ringLock = new();

    public event Action<float[]>? Phrase;
    /// <summary>Loudness of the last block (RMS), for a level meter.</summary>
    public event Action<float>? Level;

    /// <summary>While false the audio is read but thrown away (AL-76 is speaking, or paused).</summary>
    public bool Listening { get; set; } = true;
    public string DeviceName { get; private set; } = "";

    /// <summary>How the microphone is open: "aec" (Windows' voice processing with echo cancellation), "comm"
    /// (voice processing, no echo reference control) or "plain".</summary>
    public string Mode { get; private set; } = "";

    public void Start()
    {
        using var devices = new MMDeviceEnumerator();
        var device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        DeviceName = device.FriendlyName;
        try { StartCommunications(device); }
        catch (Exception e)
        {
            Log.Info($"microfono: modalità comunicazioni non disponibile ({e.Message}), uso quella normale");
            recorder?.Dispose();
            recorder = null;
            StartPlain(device);
        }
    }

    /// <summary>
    /// The communications mode asks Windows for its voice pipeline: echo cancellation (AL-76's own voice taken out
    /// of the microphone), noise suppression and gain control, where the device or Windows 11 provide them.
    /// </summary>
    private void StartCommunications(MMDevice device)
    {
        recorder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithCommunicationsMode().Build();
        recorder.DataAvailable += (buffer, flags, _, _) =>
        {
            if (recorder?.WaveFormat is not { } format) return;
            if ((flags & AudioClientBufferFlags.Silent) != 0) { OnSamples(new float[buffer.Length / Math.Max(1, format.BlockAlign) * format.Channels], format); return; }
            OnSamples(Decode(buffer, format), format);
        };
        recorder.RecordingStopped += (_, e) => { if (e.Exception is not null) Log.Info($"microfono fermato: {e.Exception.Message}"); };
        recorder.StartRecording();
        Mode = recorder.AcousticEchoCancellationControl is not null ? "aec" : "comm";
        var f = recorder.WaveFormat;
        Log.Info($"microfono: {DeviceName}, modalità {Mode}, {f.SampleRate} Hz, {f.Channels} canali, {f.BitsPerSample} bit, {f.Encoding}");
    }

    private void StartPlain(MMDevice device)
    {
        capture = new WasapiCapture(device);
        var format = capture.WaveFormat;
        Mode = "plain";
        Log.Info($"microfono: {DeviceName}, modalità normale, {format.SampleRate} Hz, {format.Channels} canali, {format.BitsPerSample} bit, {format.Encoding}");
        capture.DataAvailable += (_, e) => OnSamples(Decode(e.Buffer.AsSpan(0, e.BytesRecorded), format), format);
        capture.RecordingStopped += (_, e) => { if (e.Exception is not null) Log.Info($"microfono fermato: {e.Exception.Message}"); };
        capture.StartRecording();
    }

    private void OnSamples(float[] samples, WaveFormat format)
    {
        var mono = Wav.Resample(Wav.ToMono(samples, format.Channels), format.SampleRate, VoiceActivityDetector.SampleRate);
        if (mono.Length > 0)
        {
            double sum = 0;
            foreach (var s in mono) sum += s * s;
            Level?.Invoke((float)Math.Sqrt(sum / mono.Length));
        }
        lock (ringLock)
            foreach (var x in mono) { ring[ringEnd] = x; ringEnd = (ringEnd + 1) % ring.Length; }
        if (!Listening) return;
        foreach (var phrase in vad.Feed(mono)) Phrase?.Invoke(phrase);
    }

    private static float[] Decode(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                       || (format is WaveFormatExtensible ext && ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        if (isFloat && format.BitsPerSample == 32)
            return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer).ToArray();
        if (format.BitsPerSample == 16)
        {
            var f = new float[buffer.Length / 2];
            for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(buffer.Slice(i * 2, 2)) / 32768f;
            return f;
        }
        if (format.BitsPerSample == 24)
        {
            var f = new float[buffer.Length / 3];
            for (int i = 0; i < f.Length; i++)
                f[i] = ((buffer[i * 3] << 8) | (buffer[i * 3 + 1] << 16) | (buffer[i * 3 + 2] << 24)) / 2147483648f;
            return f;
        }
        if (format.BitsPerSample == 32)
        {
            var f = new float[buffer.Length / 4];
            for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt32(buffer.Slice(i * 4, 4)) / 2147483648f;
            return f;
        }
        return [];
    }

    /// <summary>The last <paramref name="seconds"/> of audio, 16 kHz mono (at most 3 s).</summary>
    public float[] Recent(double seconds)
    {
        int n = Math.Min(ring.Length, (int)(seconds * VoiceActivityDetector.SampleRate));
        var copy = new float[n];
        lock (ringLock)
            for (int i = 0; i < n; i++) copy[i] = ring[(ringEnd - n + i + ring.Length) % ring.Length];
        return copy;
    }

    public void Stop()
    {
        capture?.StopRecording();
        recorder?.StopRecording();
    }

    public void Dispose()
    {
        Stop();
        capture?.Dispose();
        recorder?.Dispose();
    }
}
