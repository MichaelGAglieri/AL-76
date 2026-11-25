using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AL76.Linux.App.Native;

/// <summary>
/// The GTK thread: work from other threads (voice, brains, timers) and await continuations come here through
/// g_idle_add, like WPF's Dispatcher on Windows.
/// </summary>
internal sealed class GtkLoop : SynchronizationContext
{
    public static GtkLoop Instance { get; } = new();
    private readonly ConcurrentQueue<(SendOrPostCallback fn, object? state)> queue = new();
    private static int uiThread;

    [UnmanagedCallersOnly]
    private static int Drain(IntPtr _)
    {
        while (Instance.queue.TryDequeue(out var item))
        {
            try { item.fn(item.state); }
            catch (Exception e) { AL76.Core.Diagnostics.Log.Info($"errore nel ciclo GTK: {e}"); }
        }
        return 0;   // G_SOURCE_REMOVE
    }

    private static unsafe IntPtr DrainPointer => (IntPtr)(delegate* unmanaged<IntPtr, int>)&Drain;

    public static void Install()
    {
        uiThread = Environment.CurrentManagedThreadId;
        SetSynchronizationContext(Instance);
    }

    public static bool OnUiThread => Environment.CurrentManagedThreadId == uiThread;

    public override void Post(SendOrPostCallback d, object? state)
    {
        queue.Enqueue((d, state));
        Gtk.IdleAdd(DrainPointer, IntPtr.Zero);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (OnUiThread) { d(state); return; }
        using var done = new ManualResetEventSlim();
        Post(_ => { try { d(state); } finally { done.Set(); } }, null);
        done.Wait();
    }

    /// <summary>Runs on the GTK thread (now, if already there).</summary>
    public static void Run(Action action)
    {
        if (OnUiThread) action();
        else Instance.Post(_ => action(), null);
    }

    public override SynchronizationContext CreateCopy() => this;
}
