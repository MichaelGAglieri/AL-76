using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AL76.Core.Phone;

/// <summary>
/// The wire between AL-76 and the iPhone/iPad app, the same as the Mac's (Mac: PhoneLink.swift), so the app
/// already in the phone works with the PC too. TCP on port 47831, encrypted with pre-shared-key TLS: the key
/// comes from the pairing code (100 random bits, never sent), so a finished handshake is already a paired device.
/// Frames: four bytes of length (big endian), one byte of kind, the content.
/// </summary>
public static class PhoneLink
{
    public const int Port = 47831;
    /// <summary>2 on the Mac means the screen as H.264 video; the PC does not show its screen yet, it answers so.</summary>
    public const int Version = 2;
    public const int MaxFrame = 16 * 1024 * 1024;
    public const string Identity = "jarvis";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewCode()
    {
        var chars = Enumerable.Range(0, 20).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray();
        return string.Join("-", Enumerable.Range(0, 5).Select(i => new string(chars, i * 4, 4)));
    }

    /// <summary>Without hyphens and with the letters people confuse corrected (O → 0, I and L → 1).</summary>
    public static string Normalized(string code)
    {
        var clean = new StringBuilder();
        foreach (char c in code.ToUpperInvariant())
        {
            if (c == 'O') clean.Append('0');
            else if (c is 'I' or 'L') clean.Append('1');
            else if (Alphabet.Contains(c)) clean.Append(c);
        }
        return clean.ToString();
    }

    public static bool IsValid(string code) => Normalized(code).Length == 20;

    /// <summary>HKDF-SHA256 of the code, no salt, info "jarvis-iphone-1": the same 32 bytes as CryptoKit on the phone.</summary>
    public static byte[] Key(string code) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(Normalized(code)), 32, [], Encoding.UTF8.GetBytes("jarvis-iphone-1"));

    /// <summary>The QR content: code and this PC's address (the phone looks for the Mac by name; for the PC it needs the address).</summary>
    public static string PairingLink(string code, string host) =>
        $"jarvis://pair?code={Uri.EscapeDataString(code)}&host={Uri.EscapeDataString(host)}";

    /// <summary>Only the home network and Tailscale (100.64.0.0/10): never from the internet.</summary>
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 127 || b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] is >= 16 and <= 31)
                   || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        return false;
    }

    /// <summary>This PC's address on the home network, for the QR (the first private IPv4 of an interface that is up).</summary>
    public static string? LanAddress()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                            && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsLocal(a) && !IPAddress.IsLoopback(a) && a.GetAddressBytes()[0] != 169)
                .OrderBy(a => a.GetAddressBytes()[0] == 100 ? 1 : 0)   // Tailscale after the home network
                .Select(a => a.ToString())
                .FirstOrDefault();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return null; }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<PhoneKind>))]
public enum PhoneKind
{
    // From the phone
    [JsonStringEnumMemberName("hello")] Hello,
    [JsonStringEnumMemberName("command")] Command,
    [JsonStringEnumMemberName("stop")] Stop,
    [JsonStringEnumMemberName("played")] Played,
    [JsonStringEnumMemberName("watch")] Watch,
    [JsonStringEnumMemberName("frameShown")] FrameShown,
    [JsonStringEnumMemberName("pointer")] Pointer,
    [JsonStringEnumMemberName("typing")] Typing,
    [JsonStringEnumMemberName("key")] Key,
    // To the phone
    [JsonStringEnumMemberName("welcome")] Welcome,
    [JsonStringEnumMemberName("state")] State,
    [JsonStringEnumMemberName("heard")] Heard,
    [JsonStringEnumMemberName("reply")] Reply,
    [JsonStringEnumMemberName("stopAudio")] StopAudio,
    [JsonStringEnumMemberName("notice")] Notice,
    [JsonStringEnumMemberName("watchEnded")] WatchEnded,
    [JsonStringEnumMemberName("replaced")] Replaced,
    [JsonStringEnumMemberName("dictation")] Dictation,
}

/// <summary>What the two sides tell each other, apart from audio. Missing fields are left out, as Swift's Codable does.</summary>
public sealed record PhoneMessage(
    [property: JsonPropertyName("kind")] PhoneKind Kind,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("number")] int? Number = null,
    [property: JsonPropertyName("address")] string? Address = null,
    [property: JsonPropertyName("x")] double? X = null,
    [property: JsonPropertyName("y")] double? Y = null)
{
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

    public static PhoneMessage? Decode(ReadOnlySpan<byte> json)
    {
        try { return JsonSerializer.Deserialize<PhoneMessage>(json, Options); }
        catch (JsonException) { return null; }
    }
}

/// <summary>A voice: from the phone a phrase at 16 kHz to transcribe, from the PC a sentence to play. 16-bit little endian.</summary>
public sealed record PhoneClip(uint Id, uint SampleRate, float[] Samples)
{
    public double Duration => Samples.Length / (double)Math.Max(SampleRate, 1);

    public byte[] Encode()
    {
        var data = new byte[8 + Samples.Length * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(data, Id);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), SampleRate);
        for (int i = 0; i < Samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(8 + i * 2), (short)(Math.Clamp(Samples[i], -1f, 1f) * 32767));
        return data;
    }

    public static PhoneClip? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || (data.Length - 8) % 2 != 0) return null;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (rate is < 8_000 or > 96_000) return null;
        var samples = new float[(data.Length - 8) / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(8 + i * 2)..]) / 32767f;
        return new PhoneClip(id, rate, samples);
    }
}

/// <summary>Frames over any stream (the TLS one in use, a memory one in the tests).</summary>
public static class PhoneFrames
{
    public const byte Message = 0, Clip = 1, Screen = 2, Video = 3;

    public static byte[] Frame(byte kind, byte[] payload)
    {
        var frame = new byte[payload.Length + 5];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length + 1);
        frame[4] = kind;
        payload.CopyTo(frame, 5);
        return frame;
    }

    /// <summary>The next frame, or null when the other side closed or sent something impossible.</summary>
    public static async Task<(byte kind, byte[] payload)?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await FillAsync(stream, header, ct)) return null;
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is < 1 or > PhoneLink.MaxFrame) return null;
        var body = new byte[length];
        if (!await FillAsync(stream, body, ct)) return null;
        return (body[0], body[1..]);
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
