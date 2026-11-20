using System.Security.Cryptography;
using AL76.Core.Diagnostics;

namespace AL76.Core.Phone;

/// <summary>The PC screen and its mouse and keyboard, for the phone's screen view (Windows: WindowsAppDriver).</summary>
public interface IPhoneScreen
{
    /// <summary>A JPEG of the whole screen, or null when it cannot be read.</summary>
    Task<byte[]?> CaptureAsync(CancellationToken ct);
    /// <summary>kind: click, double, right, down, move, drag, up, scroll; x and y from 0 to 1 on the picture.</summary>
    Task PointerAsync(string kind, double x, double y, int amount, CancellationToken ct);
    Task TypeAsync(string text, CancellationToken ct);
    /// <summary>return, delete, escape, tab (the phone's names).</summary>
    Task KeyAsync(string name, CancellationToken ct);
}

/// <summary>
/// The PC screen on the phone (Mac: ScreenShare, photo mode): one JPEG at a time, the next only after the phone says
/// it showed the previous ("frameShown", or 3 s), at most 10 a second. A picture equal to the last is not sent again,
/// so a still screen costs almost nothing. After a click or a key from the phone the next picture comes at once.
/// </summary>
public sealed class ScreenStreamer(IPhoneScreen screen, Action<byte[]> send, Action<string> ended)
{
    private CancellationTokenSource? running;
    private readonly SemaphoreSlim shown = new(0, 1);
    private readonly SemaphoreSlim hurry = new(0, 1);
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan ShownTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan StillInterval { get; init; } = TimeSpan.FromMilliseconds(300);
    public bool Active => running is not null;
    public int Sent { get; private set; }

    public void Start()
    {
        if (running is not null) return;
        var cts = running = new CancellationTokenSource();
        Sent = 0;
        Log.Info("telefono: schermo mostrato");
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    /// <summary>With a reason the phone closes its view and shows it.</summary>
    public void Stop(string? reason = null)
    {
        var cts = Interlocked.Exchange(ref running, null);
        if (cts is null) return;
        cts.Cancel();
        Log.Info($"telefono: schermo non più mostrato ({Sent} immagini)");
        if (reason is not null) ended(reason);
    }

    public void FrameShown() { if (shown.CurrentCount == 0) shown.Release(); }

    public void Hurry() { if (hurry.CurrentCount == 0) hurry.Release(); }

    private async Task LoopAsync(CancellationToken ct)
    {
        byte[]? lastHash = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var frame = await screen.CaptureAsync(ct);
                if (frame is null) { Stop("Lo schermo del PC non si legge."); return; }
                var hash = SHA256.HashData(frame);
                if (lastHash is not null && hash.AsSpan().SequenceEqual(lastHash))
                {
                    await WaitAsync(hurry, StillInterval, ct);
                    continue;
                }
                lastHash = hash;
                while (shown.CurrentCount > 0) await shown.WaitAsync(ct);   // a late acknowledgment is not this frame's
                send(frame);
                Sent++;
                await WaitAsync(shown, ShownTimeout, ct);
                var rest = MinInterval - clock.Elapsed;
                if (rest > TimeSpan.Zero) await WaitAsync(hurry, rest, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Info($"telefono: schermo interrotto: {e.Message}");
            Stop("Lo schermo del PC si è interrotto.");
        }
    }

    private static async Task WaitAsync(SemaphoreSlim signal, TimeSpan limit, CancellationToken ct) => await signal.WaitAsync(limit, ct);

    /// <summary>A click, text or key from the phone: done on the PC (the user makes it, seeing the screen), then a fresh picture.
    /// Text is never logged: it could be a password.</summary>
    public async Task InputAsync(PhoneMessage m, CancellationToken ct)
    {
        if (!Active) return;
        switch (m.Kind)
        {
            case PhoneKind.Pointer when m.X is double x && m.Y is double y && x is >= 0 and <= 1 && y is >= 0 and <= 1
                                        && m.Text is "click" or "double" or "right" or "down" or "move" or "drag" or "up" or "scroll":
                await screen.PointerAsync(m.Text, x, y, m.Number ?? 0, ct);
                if (m.Text is "click" or "double" or "right") Log.Audit($"{m.Text} dal telefono", $"{x:0.00},{y:0.00}");
                break;
            case PhoneKind.Typing when (m.Text ?? "").Length > 0:
                await screen.TypeAsync(m.Text!.Length > 2000 ? m.Text[..2000] : m.Text, ct);
                Log.Audit("testo dal telefono", $"{m.Text.Length} caratteri");
                break;
            case PhoneKind.Key when m.Text is "return" or "delete" or "escape" or "tab":
                await screen.KeyAsync(m.Text, ct);
                break;
            default:
                return;   // an unknown gesture from a newer app is ignored, never turned into a click
        }
        Hurry();
    }
}
