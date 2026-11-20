using System;
using System.Runtime.InteropServices;
using AL76.Core.Diagnostics;

namespace AL76.Platform;

/// <summary>
/// Announces the phone link on the home network as "_jarvis._tcp", like the Mac does with Bonjour, so the
/// iPhone/iPad app finds the PC by itself. Windows 10 1809+ has an mDNS responder built in (dnsapi.dll,
/// DnsServiceRegister): no extra service to install. If it fails the phone still connects with the QR's address.
/// </summary>
public sealed class WindowsBonjour : IDisposable
{
    private const int DnsRequestPending = 9506;
    private IntPtr instance;
    private IntPtr request;
    private Callback? keepAlive;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Callback(uint status, IntPtr context, IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    private struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr Completion;
        public IntPtr Context;
        public IntPtr Credentials;
        public int UnicastEnabled;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DnsServiceConstructInstance(string serviceName, string hostName, IntPtr ip4, IntPtr ip6,
        ushort port, ushort priority, ushort weight, uint propertiesCount, IntPtr keys, IntPtr values);

    [DllImport("dnsapi.dll")] private static extern uint DnsServiceRegister(IntPtr request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceDeRegister(IntPtr request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern void DnsServiceFreeInstance(IntPtr instance);

    /// <summary>Starts the announcement; false (and a log line) when Windows refuses it.</summary>
    public bool Start(string name, int port)
    {
        if (request != IntPtr.Zero) return true;
        try
        {
            string host = Environment.MachineName;
            instance = DnsServiceConstructInstance($"{name}._jarvis._tcp.local", $"{host}.local", IntPtr.Zero, IntPtr.Zero,
                (ushort)port, 0, 0, 0, IntPtr.Zero, IntPtr.Zero);
            if (instance == IntPtr.Zero) { Log.Info("telefono: annuncio Bonjour non creato"); return false; }
            keepAlive = (status, _, registered) =>
            {
                Log.Info(status == 0 ? "telefono: annunciato in rete come _jarvis._tcp" : $"telefono: annuncio Bonjour rifiutato ({status})");
                // "registered" may be our own instance: never freed here (a double free would crash the app).
            };
            var r = new RegisterRequest
            {
                Version = 1, ServiceInstance = instance,
                Completion = Marshal.GetFunctionPointerForDelegate(keepAlive),
            };
            request = Marshal.AllocHGlobal(Marshal.SizeOf<RegisterRequest>());
            Marshal.StructureToPtr(r, request, false);
            uint result = DnsServiceRegister(request, IntPtr.Zero);
            if (result == DnsRequestPending) return true;
            Log.Info($"telefono: annuncio Bonjour non partito ({result})");
            Free();
            return false;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Info($"telefono: annuncio Bonjour non disponibile su questo Windows: {e.Message}");
            Free();
            return false;
        }
    }

    public void Dispose()
    {
        if (request == IntPtr.Zero) return;
        // Deregistering finishes in the background and still reads the request and the instance: they are left
        // allocated (a few hundred bytes, once per switch-off) rather than freed under Windows' feet.
        try { DnsServiceDeRegister(request, IntPtr.Zero); } catch (Exception e) { Log.Info($"telefono: fine annuncio: {e.Message}"); }
        request = IntPtr.Zero;
        instance = IntPtr.Zero;
    }

    private void Free()
    {
        // The registered copy is freed in the callback; the instance we built is ours.
        if (instance != IntPtr.Zero) { DnsServiceFreeInstance(instance); instance = IntPtr.Zero; }
        if (request != IntPtr.Zero) { Marshal.FreeHGlobal(request); request = IntPtr.Zero; }
    }
}
