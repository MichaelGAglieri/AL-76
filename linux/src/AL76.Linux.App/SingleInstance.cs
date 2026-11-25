using System.Net.Sockets;
using System.Text;
using AL76.Core.Diagnostics;

namespace AL76.Linux.App;

/// <summary>
/// One AL-76 per user (Windows: a named mutex). The first one listens on a Unix socket in the user's runtime folder;
/// a second start sends its request there ("open", later "--barra" for the writing bar) and quits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly string path;
    private Socket? listener;

    private SingleInstance(string path) => this.path = path;

    /// <summary>The running copy took the message: this one should quit.</summary>
    public static bool SendToRunning(string path, string message)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(path));
            s.Send(Encoding.UTF8.GetBytes(message + "\n"));
            return true;
        }
        catch (SocketException) { return false; }   // left over from a crash: nobody listens
    }

    public static SingleInstance Listen(string path, Action<string> received)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { File.Delete(path); } catch (IOException) { }
        var me = new SingleInstance(path);
        var socket = me.listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        // Only this user may talk to it.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        socket.Listen(4);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                Socket client;
                try { client = await socket.AcceptAsync(); }
                catch (Exception e) when (e is SocketException or ObjectDisposedException) { return; }
                using (client)
                {
                    var buffer = new byte[512];
                    int n = 0;
                    try { n = await client.ReceiveAsync(buffer); } catch (SocketException) { }
                    string text = Encoding.UTF8.GetString(buffer, 0, n).Trim();
                    if (text.Length > 0) { Log.Info($"seconda istanza: {text}"); received(text); }
                }
            }
        });
        return me;
    }

    public void Dispose()
    {
        listener?.Dispose();
        try { File.Delete(path); } catch (IOException) { }
    }
}
