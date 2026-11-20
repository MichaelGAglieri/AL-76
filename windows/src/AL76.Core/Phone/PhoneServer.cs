using System.Net;
using System.Net.Sockets;
using System.Text;
using AL76.Core.Diagnostics;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace AL76.Core.Phone;

/// <summary>
/// The PC side of the link with iPhone and iPad (Mac: PhoneBridge): one paired device at a time, the last opened.
/// Windows' own TLS has no pre-shared keys, so the handshake is BouncyCastle's (MIT licence), TLS 1.2 with
/// TLS_PSK_WITH_AES_128_GCM_SHA256 like the Mac, and TLS 1.3 with the same key as external PSK.
/// When the module is off nothing listens on the network.
/// </summary>
public sealed class PhoneServer(Func<string> code, string deviceName, int port = PhoneLink.Port) : IDisposable
{
    public event Action<PhoneMessage>? Received;
    public event Action<PhoneClip>? Heard;
    /// <summary>True when a device connected, false when it left.</summary>
    public event Action<bool>? ConnectedChanged;

    private TcpListener? listener;
    private CancellationTokenSource? running;
    private Link? current;
    private readonly object gate = new();
    private uint nextClip = 1;

    public bool Listening => listener is not null;
    public bool Connected => current is not null;
    public string Problem { get; private set; } = "";
    /// <summary>The port really used (0 in the tests means "any free port").</summary>
    public int Port { get; private set; } = port;

    public void Start()
    {
        if (listener is not null) return;
        try
        {
            var l = new TcpListener(IPAddress.IPv6Any, port);
            l.Server.DualMode = true;
            l.Start();
            listener = l;
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            running = new CancellationTokenSource();
            Problem = "";
            _ = AcceptLoopAsync(l, running.Token);
            Log.Info($"telefono: in ascolto sulla porta {Port}");
        }
        catch (SocketException e)
        {
            Problem = $"porta {port} non disponibile: {e.Message}";
            Log.Info($"telefono: {Problem}");
        }
    }

    public void Stop()
    {
        running?.Cancel();
        listener?.Stop();
        listener = null;
        Link? old;
        lock (gate) { old = current; current = null; }
        if (old is not null) { old.Close(); ConnectedChanged?.Invoke(false); }
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(ct); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(() => HandleAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        if (remote is null || !PhoneLink.IsLocal(remote))
        {
            Log.Info($"telefono: rifiutato {remote} (fuori dalla rete locale)");
            client.Dispose();
            return;
        }
        client.NoDelay = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        var tls = new TlsServerProtocol(client.GetStream());
        // Whoever does not finish the handshake within 10 s is not left hanging.
        using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        using (limit.Token.Register(client.Dispose))
        {
            try { await Task.Run(() => tls.Accept(new Server(PhoneLink.Key(code()))), ct); }
            catch (Exception e)
            {
                Log.Info($"telefono: tentativo non riuscito da {remote} (codice errato o rete): {e.GetType().Name}");
                client.Dispose();
                return;
            }
        }
        var link = new Link(client, tls);
        Link? previous;
        lock (gate) { previous = current; current = link; }
        if (previous is not null) previous.SendAndClose(new PhoneMessage(PhoneKind.Replaced));
        link.Send(PhoneFrames.Message, new PhoneMessage(PhoneKind.Welcome, deviceName, PhoneLink.Version).Encode());
        Log.Info($"telefono: collegato da {remote}");
        ConnectedChanged?.Invoke(true);
        try
        {
            while (!ct.IsCancellationRequested && await PhoneFrames.ReadAsync(tls.Stream, ct) is { } frame)
            {
                if (!ReferenceEquals(current, link)) break;
                switch (frame.kind)
                {
                    case PhoneFrames.Message when PhoneMessage.Decode(frame.payload) is { } m: Received?.Invoke(m); break;
                    case PhoneFrames.Clip when PhoneClip.Decode(frame.payload) is { } clip: Heard?.Invoke(clip); break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or TlsFatalAlert or TlsNoCloseNotifyException) { }
        bool wasCurrent;
        lock (gate) { wasCurrent = ReferenceEquals(current, link); if (wasCurrent) current = null; }
        link.Close();
        if (wasCurrent) { Log.Info("telefono: scollegato"); ConnectedChanged?.Invoke(false); }
    }

    public void Send(PhoneMessage message) => current?.Send(PhoneFrames.Message, message.Encode());

    /// <summary>A JPEG of the PC screen for the phone's screen view.</summary>
    public void SendScreen(byte[] jpeg) => current?.Send(PhoneFrames.Screen, jpeg);

    /// <summary>A sentence to play on the phone; returns its id (the phone answers "played" with it).</summary>
    public uint Send(float[] samples, int rate)
    {
        uint id;
        lock (gate) id = nextClip++;
        current?.Send(PhoneFrames.Clip, new PhoneClip(id, (uint)rate, samples).Encode());
        return id;
    }

    private sealed class Link(TcpClient client, TlsServerProtocol tls)
    {
        private readonly object sending = new();
        private bool closed;

        public void Send(byte kind, byte[] payload)
        {
            lock (sending)
            {
                if (closed) return;
                try { tls.Stream.Write(PhoneFrames.Frame(kind, payload)); tls.Stream.Flush(); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or TlsFatalAlert) { closed = true; client.Dispose(); }
            }
        }

        public void SendAndClose(PhoneMessage message)
        {
            Send(PhoneFrames.Message, message.Encode());
            Close();
        }

        public void Close()
        {
            lock (sending)
            {
                if (closed) return;
                closed = true;
                try { tls.Close(); } catch (Exception) { }
                client.Dispose();
            }
        }
    }

    /// <summary>The handshake's rules: only the pairing key, identity "jarvis".</summary>
    private sealed class Server(byte[] key) : PskTlsServer(new BcTlsCrypto(new SecureRandom()), new Identities(key))
    {
        protected override int[] GetSupportedCipherSuites() =>
            [CipherSuite.TLS_AES_128_GCM_SHA256, CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256];

        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv13.DownTo(ProtocolVersion.TLSv12);

        public override TlsPskExternal GetExternalPsk(IList<PskIdentity> identities)
        {
            foreach (var id in identities)
                if (Encoding.UTF8.GetString(id.Identity) == PhoneLink.Identity)
                    return new BasicTlsPskExternal(id.Identity, Crypto.CreateSecret(key), PrfAlgorithm.tls13_hkdf_sha256);
            return null!;
        }
    }

    private sealed class Identities(byte[] key) : TlsPskIdentityManager
    {
        public byte[]? GetHint() => null;
        public byte[]? GetPsk(byte[] identity) => Encoding.UTF8.GetString(identity) == PhoneLink.Identity ? key : null;
    }
}
