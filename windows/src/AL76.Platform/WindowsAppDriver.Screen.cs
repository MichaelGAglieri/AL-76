using System.Runtime.InteropServices;
using System.Windows.Automation;
using AL76.Core.Diagnostics;
using AL76.Core.Maker;
using AL76.Core.Tools;
using AL76.Core.Voice;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AL76.Platform;

/// <summary>
/// Apps whose controls UI Automation does not show (WhatsApp for Windows showed only its three window buttons on a
/// tester's PC, 26/11): typing where the cursor is, and the window's text read with Windows' OCR.
/// </summary>
public sealed partial class WindowsAppDriver
{
    public Task<string?> TypeFocusedAsync(AppWindow window, string text, bool submit, CancellationToken ct) => Run<string?>(() =>
    {
        var h = (IntPtr)window.Handle;
        if (!IsWindow(h)) return "The window is closed.";
        if (!Front(h)) return "Could not bring the app in front: the user may be using another window.";
        Thread.Sleep(80);
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is not null && focused.Current.IsPassword) return "The cursor is in a password field: AL-76 never types there.";
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
        int sent = 0;
        foreach (var chunk in text.Replace("\r\n", "\n").Chunk(16))
        {
            if (ct.IsCancellationRequested) return $"Stopped by the user after {sent} characters.";
            // The user clicked elsewhere: never type into a window they did not expect.
            if (Root(GetForegroundWindow()) != h) return $"Stopped: another window came in front after {sent} characters.";
            foreach (char ch in chunk)
            {
                if (ch == '\n') Keys(new KeyCombo("enter", false, false, true, false));   // a new line, not "send"
                else Unicode(ch);
                Thread.Sleep(12);
            }
            sent += chunk.Length;
        }
        if (submit)
        {
            Thread.Sleep(120);
            if (Root(GetForegroundWindow()) != h) return "Typed, but another window came in front before Enter.";
            Keys(new KeyCombo("enter", false, false, false, false));
        }
        return null;
    }, ct);

    public async Task<WindowText?> ReadTextAsync(AppWindow window, CancellationToken ct)
    {
        if (Ocr.Value is not { } ocr) return null;
        var shot = await Run(() =>
        {
            var h = (IntPtr)window.Handle;
            if (!IsWindow(h) || IsIconic(h)) return null;
            var f = Frame(h);
            double scale = Math.Max(1, GetDpiForWindow(h) / 96.0);
            return RegionPixels(f.Left, f.Top, f.Right - f.Left, f.Bottom - f.Top) is { } p ? (p, scale) : ((Pixels, double)?)null;
        }, ct);
        if (shot is not var (pixels, dpi)) return null;
        if (Math.Max(pixels.Width, pixels.Height) > OcrEngine.MaxImageDimension) return null;
        // Small interface text reads better twice as large.
        int up = dpi < 1.5 && Math.Max(pixels.Width, pixels.Height) * 2 <= OcrEngine.MaxImageDimension ? 2 : 1;
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
            var lines = new List<ScreenWord>();
            foreach (var line in result.Lines)
            {
                if (line.Words.Count == 0) continue;
                double left = line.Words.Min(w => w.BoundingRect.X), top = line.Words.Min(w => w.BoundingRect.Y);
                double right = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width), bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                lines.Add(new ScreenWord(line.Text, area.Left + left / up, area.Top + top / up, (right - left) / up, (bottom - top) / up));
            }
            return new WindowText(lines, area);
        }
        catch (Exception e) when (e is COMException or ArgumentException or InvalidOperationException)
        {
            Log.Info($"app: lettura dello schermo non riuscita: {e.Message}");
            return null;
        }
    }
}
