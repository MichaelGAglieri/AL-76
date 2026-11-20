namespace AL76.Core.Voice;

/// <summary>Reads and writes WAV files: the test mode feeds phrases from files instead of the microphone.</summary>
public static class Wav
{
    /// <summary>Reads PCM 16-bit or float 32-bit WAV, any rate and channels, and returns 16 kHz mono.</summary>
    public static float[] ReadMono16k(string path)
    {
        using var file = File.OpenRead(path);
        var (mono, rate) = Read(file);
        return Resample(mono, rate, VoiceActivityDetector.SampleRate);
    }

    /// <summary>Mono samples at the file's own rate (the Windows voice comes as a WAV stream).</summary>
    public static (float[] mono, int rate) Read(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("not a WAV file");
        r.ReadInt32();
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("not a WAV file");
        int format = 1, channels = 1, rate = 16000, bits = 16;
        float[]? data = null;
        while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
        {
            string id = new(r.ReadChars(4));
            int size = r.ReadInt32();
            long next = r.BaseStream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                format = r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32();
                r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                if (format == unchecked((short)0xFFFE) || format == -2) format = bits == 32 ? 3 : 1; // extensible
            }
            else if (id == "data")
            {
                int count = size / (bits / 8);
                data = new float[count];
                for (int i = 0; i < count; i++)
                    data[i] = format == 3 && bits == 32 ? r.ReadSingle()
                            : bits == 16 ? r.ReadInt16() / 32768f
                            : bits == 32 ? r.ReadInt32() / 2147483648f
                            : (r.ReadByte() - 128) / 128f;
            }
            r.BaseStream.Position = Math.Min(next, r.BaseStream.Length);
        }
        if (data is null) throw new InvalidDataException("WAV without data");
        return (ToMono(data, channels), rate);
    }

    public static float[] ToMono(float[] interleaved, int channels)
    {
        if (channels <= 1) return interleaved;
        var mono = new float[interleaved.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }

    /// <summary>
    /// Linear resampling: good enough for speech recognition. Exact integer downsampling (the microphone's
    /// 48 kHz to 16 kHz) averages each group instead, which keeps high hiss out of the speech band.
    /// </summary>
    public static float[] Resample(float[] input, int from, int to)
    {
        if (from == to || input.Length == 0) return input;
        if (from > to && from % to == 0)
        {
            int k = from / to;
            var avg = new float[input.Length / k];
            for (int i = 0; i < avg.Length; i++)
            {
                float sum = 0;
                for (int j = 0; j < k; j++) sum += input[i * k + j];
                avg[i] = sum / k;
            }
            return avg;
        }
        int n = (int)((long)input.Length * to / from);
        var output = new float[n];
        double step = (double)from / to;
        for (int i = 0; i < n; i++)
        {
            double pos = i * step;
            int a = (int)pos;
            int b = Math.Min(a + 1, input.Length - 1);
            double t = pos - a;
            output[i] = (float)(input[a] * (1 - t) + input[b] * t);
        }
        return output;
    }

    /// <summary>Writes 16-bit PCM mono.</summary>
    public static void Write(string path, float[] samples, int rate) => File.WriteAllBytes(path, ToBytes(samples, rate));

    /// <summary>A whole 16-bit PCM mono WAV in memory, for playback.</summary>
    public static byte[] ToBytes(float[] samples, int rate)
    {
        using var memory = new MemoryStream(44 + samples.Length * 2);
        using (var w = new BinaryWriter(memory, System.Text.Encoding.ASCII, leaveOpen: true))
            WriteTo(w, samples, rate);
        return memory.ToArray();
    }

    private static void WriteTo(BinaryWriter w, float[] samples, int rate)
    {
        int bytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in samples) w.Write((short)Math.Clamp(s * 32767f, -32768f, 32767f));
    }
}
