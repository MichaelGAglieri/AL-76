using System.Net.Sockets;
using System.Text;
using AL76.Core.Phone;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace AL76.Core.Tests;

public class PhoneTests
{
    [Fact]
    public void CodesAndTheKeyMatchTheMac()
    {
        string code = PhoneLink.NewCode();
        Assert.Matches("^([0-9A-HJKMNP-TV-Z]{4}-){4}[0-9A-HJKMNP-TV-Z]{4}$", code);
        Assert.Equal("0110ABCD", PhoneLink.Normalized("oIlo-abcd"));
        Assert.False(PhoneLink.IsValid("1234"));
        // RFC 5869 HKDF-SHA256 with empty salt, the same as CryptoKit's HKDF<SHA256>.deriveKey without salt.
        Assert.Equal("460c8f3eabe9234ff413bbf76ac3083303a67d7383542c6a9a51e421a9209c91", Convert.ToHexStringLower(PhoneLink.Key("AAAA-BBBB-CCCC-DDDD-EEEE")));
        Assert.Equal(PhoneLink.Key("aaaa bbbb cccc dddd eeee"), PhoneLink.Key("AAAA-BBBB-CCCC-DDDD-EEEE"));
        Assert.Equal("jarvis://pair?code=AAAA-BBBB&host=10.0.0.20", PhoneLink.PairingLink("AAAA-BBBB", "10.0.0.20"));
    }

    [Fact]
    public void OnlyHomeAndTailscaleAddresses()
    {
        foreach (var ok in new[] { "10.0.0.2", "172.20.1.1", "100.101.1.1", "127.0.0.1", "::1", "fe80::1", "::ffff:10.1.2.3" })
            Assert.True(PhoneLink.IsLocal(System.Net.IPAddress.Parse(ok)), ok);
        Assert.True(PhoneLink.IsLocal(new System.Net.IPAddress(new byte[] { 192, 168, 1, 5 })));   // the usual home network
        foreach (var no in new[] { "8.8.8.8", "172.32.0.1", "100.128.0.1", "2001:db8::1" })
            Assert.False(PhoneLink.IsLocal(System.Net.IPAddress.Parse(no)), no);
    }

    [Fact]
    public void MessagesAndClipsLikeSwiftCodable()
    {
        var m = new PhoneMessage(PhoneKind.StopAudio);
        Assert.Equal("{\"kind\":\"stopAudio\"}", Encoding.UTF8.GetString(m.Encode()));
        var back = PhoneMessage.Decode("{\"kind\":\"command\",\"text\":\"che ore sono\"}"u8);
        Assert.Equal(new PhoneMessage(PhoneKind.Command, "che ore sono"), back);
        Assert.Null(PhoneMessage.Decode("{\"kind\":\"sconosciuto\"}"u8));
        var clip = new PhoneClip(7, 16000, [0f, 0.5f, -1f, 1.5f]);
        var decoded = PhoneClip.Decode(clip.Encode())!;
        Assert.Equal((7u, 16000u), (decoded.Id, decoded.SampleRate));
        Assert.Equal(1f, decoded.Samples[3], 3);
        Assert.Equal(-1f, decoded.Samples[2], 3);
        Assert.Null(PhoneClip.Decode(new byte[] { 1, 0, 0, 0, 1, 0, 0, 0 }));   // 1 Hz
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PairedPhoneTalksOverPskTls(bool tls13)
    {
        string code = PhoneLink.NewCode();
        using var server = new PhoneServer(() => code, "PC di prova", port: 0);
        var got = new TaskCompletionSource<PhoneMessage>();
        var heard = new TaskCompletionSource<PhoneClip>();
        server.Received += m => got.TrySetResult(m);
        server.Heard += c => heard.TrySetResult(c);
        server.Start();

        var (tcp, stream) = Connect(server.Port, code, tls13);
        using (tcp)
        {
            var welcome = await PhoneFrames.ReadAsync(stream, CancellationToken.None);
            Assert.Equal(PhoneFrames.Message, welcome!.Value.kind);
            Assert.Equal(new PhoneMessage(PhoneKind.Welcome, "PC di prova", PhoneLink.Version), PhoneMessage.Decode(welcome.Value.payload));
            stream.Write(PhoneFrames.Frame(PhoneFrames.Message, new PhoneMessage(PhoneKind.Command, "ciao").Encode()));
            stream.Write(PhoneFrames.Frame(PhoneFrames.Clip, new PhoneClip(1, 16000, new float[160]).Encode()));
            stream.Flush();
            Assert.Equal("ciao", (await got.Task.WaitAsync(TimeSpan.FromSeconds(5))).Text);
            Assert.Equal(160, (await heard.Task.WaitAsync(TimeSpan.FromSeconds(5))).Samples.Length);
            Assert.True(server.Connected);
            server.Send(new PhoneMessage(PhoneKind.Reply, "Sono le 10."));
            var reply = await PhoneFrames.ReadAsync(stream, CancellationToken.None);
            Assert.Equal("Sono le 10.", PhoneMessage.Decode(reply!.Value.payload)!.Text);
        }
    }

    [Fact]
    public async Task WrongCodeNeverConnectsAndANewPhoneReplacesTheOld()
    {
        string code = PhoneLink.NewCode();
        using var server = new PhoneServer(() => code, "PC", port: 0);
        server.Start();
        Assert.ThrowsAny<Exception>(() => Connect(server.Port, PhoneLink.NewCode(), false));
        Assert.False(server.Connected);

        var (first, firstStream) = Connect(server.Port, code, false);
        await PhoneFrames.ReadAsync(firstStream, CancellationToken.None);
        var (second, secondStream) = Connect(server.Port, code, true);
        await PhoneFrames.ReadAsync(secondStream, CancellationToken.None);
        var replaced = await PhoneFrames.ReadAsync(firstStream, CancellationToken.None);
        Assert.Equal(PhoneKind.Replaced, PhoneMessage.Decode(replaced!.Value.payload)!.Kind);
        Assert.True(server.Connected);
        first.Dispose();
        second.Dispose();
    }

    private static (TcpClient, Stream) Connect(int port, string code, bool tls13)
    {
        var tcp = new TcpClient("127.0.0.1", port);
        var protocol = new TlsClientProtocol(tcp.GetStream());
        var crypto = new BcTlsCrypto(new SecureRandom());
        byte[] key = PhoneLink.Key(code);
        try { protocol.Connect(tls13 ? new Client13(crypto, key) : new Client12(crypto, key)); }
        catch { tcp.Dispose(); throw; }
        return (tcp, protocol.Stream);
    }

    /// <summary>The Mac's and the phone's offer: TLS 1.2 with TLS_PSK_WITH_AES_128_GCM_SHA256.</summary>
    private sealed class Client12(TlsCrypto crypto, byte[] key) : PskTlsClient(crypto, new BasicTlsPskIdentity(PhoneLink.Identity, key))
    {
        protected override int[] GetSupportedCipherSuites() => [CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256];
        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv12.Only();
    }

    /// <summary>A TLS 1.3 client with the key as external PSK, as a recent Apple stack might do.</summary>
    private sealed class Client13(TlsCrypto crypto, byte[] key) : AbstractTlsClient(crypto)
    {
        protected override int[] GetSupportedCipherSuites() => [CipherSuite.TLS_AES_128_GCM_SHA256];
        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv13.Only();
        public override IList<TlsPskExternal> GetExternalPsks() =>
            [new BasicTlsPskExternal(Encoding.UTF8.GetBytes(PhoneLink.Identity), Crypto.CreateSecret(key), PrfAlgorithm.tls13_hkdf_sha256)];
        public override TlsAuthentication GetAuthentication() => throw new TlsFatalAlert(AlertDescription.internal_error);
    }
}

public class ScreenStreamerTests
{
    private sealed class FakeScreen : IPhoneScreen
    {
        public int Captures;
        public byte Picture = 1;
        public List<string> Done { get; } = [];
        public Task<byte[]?> CaptureAsync(CancellationToken ct) { Interlocked.Increment(ref Captures); return Task.FromResult<byte[]?>([0xFF, 0xD8, Picture]); }
        public Task PointerAsync(string kind, double x, double y, int amount, CancellationToken ct) { lock (Done) Done.Add(FormattableString.Invariant($"{kind} {x:0.00},{y:0.00} {amount}")); return Task.CompletedTask; }
        public Task TypeAsync(string text, CancellationToken ct) { lock (Done) Done.Add($"type {text}"); return Task.CompletedTask; }
        public Task KeyAsync(string name, CancellationToken ct) { lock (Done) Done.Add($"key {name}"); return Task.CompletedTask; }
    }

    [Fact]
    public async Task OneFrameAtATimeStillScreenNotResentInputHurries()
    {
        var screen = new FakeScreen();
        var sent = new List<byte>();
        var ended = new List<string>();
        var s = new ScreenStreamer(screen, f => { lock (sent) sent.Add(f[2]); }, ended.Add)
        {
            MinInterval = TimeSpan.FromMilliseconds(10), ShownTimeout = TimeSpan.FromSeconds(5), StillInterval = TimeSpan.FromMilliseconds(20),
        };
        s.Start();
        await Task.Delay(300);
        Assert.Equal([1], sent);                 // waits for frameShown
        s.FrameShown();
        await Task.Delay(300);
        Assert.Equal([1], sent);                 // same picture: not sent again, but captured again
        Assert.True(screen.Captures > 3);
        screen.Picture = 2;
        await Task.Delay(200);
        Assert.Equal([1, 2], sent);
        await s.InputAsync(new PhoneMessage(PhoneKind.Pointer, "click", X: 0.5, Y: 0.25), CancellationToken.None);
        await s.InputAsync(new PhoneMessage(PhoneKind.Pointer, "lancia", X: 0.5, Y: 0.25), CancellationToken.None);
        await s.InputAsync(new PhoneMessage(PhoneKind.Pointer, "click", X: 1.5, Y: 0.25), CancellationToken.None);
        await s.InputAsync(new PhoneMessage(PhoneKind.Typing, "ciao"), CancellationToken.None);
        await s.InputAsync(new PhoneMessage(PhoneKind.Key, "return"), CancellationToken.None);
        await s.InputAsync(new PhoneMessage(PhoneKind.Key, "f4"), CancellationToken.None);
        Assert.Equal(["click 0.50,0.25 0", "type ciao", "key return"], screen.Done);
        s.Stop("chiuso");
        Assert.False(s.Active);
        Assert.Equal(["chiuso"], ended);
        await s.InputAsync(new PhoneMessage(PhoneKind.Typing, "dopo"), CancellationToken.None);
        Assert.Equal(3, screen.Done.Count);       // nothing reaches the PC once the view is closed
    }
}
