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
    /// <summary>Every way to open a microphone gave only digital silence (Windows' privacy switch, a muted or
    /// disabled input): true; a signal arrived afterwards: false.</summary>
    public event Action<bool>? AllSilent;

    /// <summary>While false the audio is read but thrown away (AL-76 is speaking, or paused).</summary>
    public bool Listening { get; set; } = true;
    public string DeviceName { get; private set; } = "";

    /// <summary>How the microphone is open: "aec" (Windows' voice processing with echo cancellation), "comm"
    /// (voice processing, no echo reference control) or "plain".</summary>
    public string Mode { get; private set; } = "";

    /// <summary>The ways to open a microphone, in order: Windows' default input (the one in Settings › Sound) with
    /// voice processing, then without, then the communications default, then any other active input. A tester's PC
    /// gave only silence (23/11): a step that hears nothing but digital silence for 8 s gives way to the next.</summary>
    private readonly List<(string id, string name, bool voice)> plan = [];
    private int step;
    private DateTime stepStarted;
    private bool heard;
    private int switching;
    private const double SilentSeconds = 8;
    private bool reportedSilent;

    // The level for the log in the first two minutes (25/11: "it does not hear me", no numbers to tell why).
    private readonly DateTime opened = DateTime.UtcNow;
    private DateTime statsFrom = DateTime.UtcNow;
    private double statsPeak;

    public void Start()
    {
        using var devices = new MMDeviceEnumerator();
        MMDevice? Default(Role role) { try { return devices.GetDefaultAudioEndpoint(DataFlow.Capture, role); } catch (Exception) { return null; } }
        var main = Default(Role.Console);
        var talk = Default(Role.Communications);
        var all = devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        Log.Info($"microfono: trovati {all.Count}: {string.Join("; ", all.Select(d => d.FriendlyName + (d.ID == main?.ID ? " [predefinito]" : "") + (d.ID == talk?.ID ? " [comunicazioni]" : "")))}");
        void Add(MMDevice? d, bool voice) { if (d is not null && !plan.Any(p => p.id == d.ID && p.voice == voice)) plan.Add((d.ID, d.FriendlyName, voice)); }
        Add(main, true);
        Add(main, false);
        Add(talk, true);
        Add(talk, false);
        foreach (var d in all.Where(d => !Loopback(d.FriendlyName))) Add(d, false);
        if (plan.Count == 0) throw new InvalidOperationException("nessun microfono attivo");
        Open(0);
    }

    /// <summary>"Stereo Mix" and the like record what the PC plays, not a voice.</summary>
    private static bool Loopback(string name) =>
        new[] { "stereo mix", "missaggio stereo", "what u hear", "wave out", "loopback" }.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

    private void Open(int index)
    {
        for (int i = index; i < plan.Count; i++)
        {
            step = i;
            heard = false;
            stepStarted = DateTime.UtcNow;
            var (id, name, voice) = plan[i];
            DeviceName = name;
            try
            {
                using var devices = new MMDeviceEnumerator();
                var device = devices.GetDevice(id);
                if (voice) StartCommunications(device); else StartPlain(device);
                return;
            }
            catch (Exception e)
            {
                Log.Info($"microfono: {name} {(voice ? "con elaborazione voce" : "normale")} non disponibile ({e.Message})");
                Close();
            }
        }
        throw new InvalidOperationException("nessun microfono si apre");
    }

    private void Close()
    {
        try { capture?.StopRecording(); } catch (Exception) { }
        try { recorder?.StopRecording(); } catch (Exception) { }
        capture?.Dispose();
        recorder?.Dispose();
        capture = null;
        recorder = null;
    }

    /// <summary>Only digital silence so far: the next way, on a pool thread (never stop a capture from its own callback).</summary>
    private void Watch(float[] raw)
    {
        if (heard) return;
        float peak = 0;
        foreach (var x in raw) { float a = Math.Abs(x); if (a > peak) peak = a; }
        if (peak > 1e-5f)
        {
            heard = true;
            Log.Info($"microfono: segnale da {DeviceName} ({Mode})");
            if (reportedSilent) { reportedSilent = false; AllSilent?.Invoke(false); }
            return;
        }
        if ((DateTime.UtcNow - stepStarted).TotalSeconds < SilentSeconds) return;
        if (step + 1 >= plan.Count)
        {
            if (!reportedSilent)
            {
                reportedSilent = true;
                Log.Info($"microfono: da nessun ingresso arriva suono (privacy di Windows: {MicrophonePrivacy.Blocked() ?? "consentito"})");
                AllSilent?.Invoke(true);
            }
            return;
        }
        if (Interlocked.Exchange(ref switching, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                Log.Info($"microfono: da {DeviceName} ({Mode}) solo silenzio per {SilentSeconds:0} s, provo il prossimo");
                Close();
                Open(step + 1);
            }
            catch (Exception e) { Log.Info($"microfono: nessun altro modo ({e.Message})"); }
            finally { Interlocked.Exchange(ref switching, 0); }
        });
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
        recorder.RecordingStopped += (_, e) => { if (e.Exception is not null) Lost(e.Exception); };
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
        capture.RecordingStopped += (_, e) => { if (e.Exception is not null) Lost(e.Exception); };
        capture.StartRecording();
    }

    private void OnSamples(float[] samples, WaveFormat format)
    {
        Watch(samples);
        var mono = Wav.Resample(Wav.ToMono(samples, format.Channels), format.SampleRate, VoiceActivityDetector.SampleRate);
        if (mono.Length > 0)
        {
            double sum = 0;
            foreach (var s in mono) sum += s * s;
            float rms = (float)Math.Sqrt(sum / mono.Length);
            Level?.Invoke(rms);
            LogLevels(rms);
        }
        lock (ringLock)
            foreach (var x in mono) { ring[ringEnd] = x; ringEnd = (ringEnd + 1) % ring.Length; }
        if (!Listening) return;
        foreach (var phrase in vad.Feed(mono)) Phrase?.Invoke(phrase);
    }

    /// <summary>Every 20 s in the first two minutes: the loudest block and the room's floor, as the VAD sees them
    /// (a phrase starts above 3.5 times the floor, at least 0.004).</summary>
    private void LogLevels(float rms)
    {
        var now = DateTime.UtcNow;
        if ((now - opened).TotalMinutes > 2) return;
        statsPeak = Math.Max(statsPeak, rms);
        if ((now - statsFrom).TotalSeconds < 20) return;
        Log.Info($"microfono: livello massimo {statsPeak:0.0000}, fondo {vad.NoiseFloor:0.0000} in 20 s ({DeviceName}, {Mode})");
        statsPeak = 0;
        statsFrom = now;
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

    private volatile bool disposed;

    /// <summary>The input went away (a controller's headset switched off, a USB microphone pulled out): a tester's
    /// AL-76 stayed deaf until restarted (23/11). The ways are looked for again, every few seconds until one opens.</summary>
    private void Lost(Exception e)
    {
        Log.Info($"microfono fermato: {e.Message}");
        if (disposed || Interlocked.Exchange(ref switching, 1) == 1) return;
        Task.Run(async () =>
        {
            try
            {
                for (int attempt = 1; !disposed; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 2 : 5));
                    if (disposed) return;
                    try
                    {
                        Close();
                        plan.Clear();
                        Start();
                        Log.Info($"microfono: riaperto dopo la disconnessione ({DeviceName})");
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 1 || attempt % 12 == 0) Log.Info($"microfono: ancora nessun ingresso ({ex.Message}), riprovo");
                    }
                }
            }
            finally { Interlocked.Exchange(ref switching, 0); }
        });
    }

    public void Stop()
    {
        capture?.StopRecording();
        recorder?.StopRecording();
    }

    public void Dispose()
    {
        disposed = true;
        Stop();
        Close();
    }
}
