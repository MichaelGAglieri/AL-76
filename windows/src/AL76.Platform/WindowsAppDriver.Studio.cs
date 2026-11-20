using System.Diagnostics;
using System.Runtime.InteropServices;
using AL76.Core.Diagnostics;
using AL76.Core.Maker;
using AL76.Core.Voice;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AL76.Platform;

/// <summary>
/// Bambu Studio seen and pressed (Mac: the screen side of BambuStudioControl). Its buttons have no accessible names, so
/// the front window's screen area is captured (what the user sees, AL-76's windows left out) and read with the OCR
/// built into Windows. Every point is in physical pixels, like the rest of this driver; the rules live in
/// <see cref="BambuStudio"/>.
/// </summary>
public sealed partial class WindowsAppDriver : IStudioDriver
{
    private static readonly Lazy<OcrEngine?> Ocr = new(() =>
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? (OcrEngine.IsLanguageSupported(new Language("it-IT")) ? OcrEngine.TryCreateFromLanguage(new Language("it-IT")) : null)
                ?? (OcrEngine.IsLanguageSupported(new Language("en-US")) ? OcrEngine.TryCreateFromLanguage(new Language("en-US")) : null);
        }
        catch (Exception e) { Log.Info($"bambu: OCR non disponibile: {e.Message}"); return null; }
    });

    public IReadOnlyList<int> Instances()
    {
        var list = new List<(int pid, DateTime start)>();
        foreach (var p in Process.GetProcessesByName("bambu-studio"))
        {
            using (p)
            {
                try { if (!p.HasExited) list.Add((p.Id, p.StartTime)); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { list.Add((p.Id, DateTime.MaxValue)); }
            }
        }
        return list.OrderBy(x => x.start).Select(x => x.pid).ToList();
    }

    /// <summary>That process's first window in z-order: a warning above the main window hides what is below.</summary>
    private static IntPtr TopHandle(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint owner);
            if (owner != pid || !IsWindowVisible(h) || IsIconic(h)) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var f = Frame(h);
            if (f.Right - f.Left <= 150 || f.Bottom - f.Top <= 80) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public (Area frame, string title)? TopWindow(int pid) => Aware<(Area, string)?>(() =>
    {
        var h = TopHandle(pid);
        if (h == IntPtr.Zero) return null;
        var f = Frame(h);
        return (new Area(f.Left, f.Top, f.Right - f.Left, f.Bottom - f.Top), Title(h));
    });

    /// <summary>Like <c>Run</c>, on the calling thread: per-monitor DPI aware for the call, then as before.</summary>
    private static T Aware<T>(Func<T> work)
    {
        IntPtr old = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try { return work(); }
        finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
    }

    public async Task<StudioScreen?> ReadAsync(int pid, CancellationToken ct)
    {
        if (Ocr.Value is not { } ocr) return null;
        var shot = await Run(() =>
        {
            var h = TopHandle(pid);
            if (h == IntPtr.Zero) return null;
            var f = Frame(h);
            double scale = Math.Max(1, GetDpiForWindow(h) / 96.0);
            return RegionPixels(f.Left, f.Top, f.Right - f.Left, f.Bottom - f.Top) is { } p ? (p, scale) : ((Pixels, double)?)null;
        }, ct);
        if (shot is not var (pixels, dpi)) return null;
        // Small interface text reads better twice as large.
        int up = dpi < 1.5 && Math.Max(pixels.Width, pixels.Height) * 2 <= OcrEngine.MaxImageDimension ? 2 : 1;
        if (Math.Max(pixels.Width, pixels.Height) > OcrEngine.MaxImageDimension) return null;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)pixels.Width, (uint)pixels.Height, 96, 96, pixels.Bgra);
            encoder.BitmapTransform.ScaledWidth = (uint)(pixels.Width * up);
            encoder.BitmapTransform.ScaledHeight = (uint)(pixels.Height * up);
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await encoder.FlushAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await ocr.RecognizeAsync(bitmap);
            var area = pixels.Area;
            var words = result.Lines.SelectMany(l => l.Words).Select(w => new ScreenWord(w.Text,
                area.Left + w.BoundingRect.X / up, area.Top + w.BoundingRect.Y / up, w.BoundingRect.Width / up, w.BoundingRect.Height / up)).ToList();
            return new StudioScreen(words, area, dpi, w => Green(pixels, w, dpi));
        }
        catch (Exception e) when (e is COMException or ArgumentException or InvalidOperationException)
        {
            Log.Info($"bambu: lettura non riuscita: {e.Message}");
            return null;
        }
    }

    /// <summary>The average colour around the label, its padding included: green when green clearly leads.</summary>
    private static bool Green(Pixels p, ScreenWord w, double dpi)
    {
        int x0 = (int)Math.Clamp(w.Left - 12 * dpi - p.Area.Left, 0, p.Width - 1), x1 = (int)Math.Clamp(w.Left + w.Width + 12 * dpi - p.Area.Left, 1, p.Width);
        int y0 = (int)Math.Clamp(w.Top - p.Area.Top, 0, p.Height - 1), y1 = (int)Math.Clamp(w.Top + w.Height - p.Area.Top, 1, p.Height);
        double r = 0, g = 0, b = 0;
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * p.Width + x) * 4;
                b += p.Bgra[i]; g += p.Bgra[i + 1]; r += p.Bgra[i + 2];
                n++;
            }
        if (n == 0) return false;
        return g > r * 1.3 && g > b * 1.3;
    }

    /// <summary>A screen area, with AL-76's own windows left out of the picture for that moment.</summary>
    private static Pixels? RegionPixels(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return null;
        var mine = OwnWindows();
        foreach (var m in mine) SetWindowDisplayAffinity(m, WDA_EXCLUDEFROMCAPTURE);
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, h);
        try
        {
            if (mine.Count > 0) Thread.Sleep(80);   // DWM applies it on the next frame
            var old = SelectObject(mem, bmp);
            bool ok = BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY | CAPTUREBLT);
            SelectObject(mem, old);
            return ok ? new Pixels(Bits(screen, bmp, w, h), w, h, new Area(x, y, w, h)) : null;
        }
        finally
        {
            foreach (var m in mine) SetWindowDisplayAffinity(m, WDA_NONE);
            DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public Task<bool> FrontAsync(int pid, CancellationToken ct) => Run(() => TopHandle(pid) is var h && h != IntPtr.Zero && Front(h), ct);

    public int? FrontPid()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        GetWindowThreadProcessId(h, out uint pid);
        return pid == 0 ? null : (int)pid;
    }

    public int? OwnerAt(double x, double y) => Aware<int?>(() =>
    {
        var at = WindowFromPoint(new POINT { X = (int)Math.Round(x), Y = (int)Math.Round(y) });
        if (at == IntPtr.Zero) return null;
        GetWindowThreadProcessId(Root(at), out uint pid);
        return pid == 0 ? null : (int)pid;
    });

    public Task MoveAsync(double x, double y, CancellationToken ct) => Run(() => SetCursorPos((int)Math.Round(x), (int)Math.Round(y)), ct);

    public Task ClickAsync(double x, double y, CancellationToken ct) => Run(() => { Click((int)Math.Round(x), (int)Math.Round(y)); return true; }, ct);

    public Task ScrollRightAsync(double x, double y, CancellationToken ct) => Run(() =>
    {
        SetCursorPos((int)Math.Round(x), (int)Math.Round(y));
        Thread.Sleep(100);
        Send([Mouse(MOUSEEVENTF_HWHEEL, 240)]);
        return true;
    }, ct);

    public bool CloseWindow(int pid, string[] titleStarts)
    {
        bool closed = false;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint owner);
            if (owner != pid || !IsWindowVisible(h)) return true;
            string title = Title(h);
            if (titleStarts.Any(t => title.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
            {
                PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                closed = true;
            }
            return true;
        }, IntPtr.Zero);
        return closed;
    }

    public string? Program()
    {
        foreach (var p in Process.GetProcessesByName("bambu-studio"))
            using (p)
                try { if (p.MainModule?.FileName is { Length: > 0 } f) return f; }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        ];
        return roots.Where(r => r.Length > 0).Select(r => Path.Combine(r, "Bambu Studio", "bambu-studio.exe")).FirstOrDefault(File.Exists);
    }

    public bool Launch(string? file)
    {
        if (Program() is not { } exe) return false;
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            if (file is not null) start.ArgumentList.Add(file);
            using var _ = Process.Start(start);
            Log.Audit("bambu studio aperto", file is null ? "" : Path.GetFileName(file));
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Info($"bambu: avvio non riuscito: {e.Message}");
            return false;
        }
    }

    private const uint MOUSEEVENTF_HWHEEL = 0x1000, WM_CLOSE = 0x0010;
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
