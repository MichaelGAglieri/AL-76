using System.Runtime.InteropServices;
using AL76.Core.Diagnostics;
using AL76.Core.Notes;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using PdfDoc = Windows.Data.Pdf.PdfDocument;
using PdfRenderOptions = Windows.Data.Pdf.PdfPageRenderOptions;

namespace AL76.Platform;

/// <summary>
/// Scanned PDF pages read with the OCR built into Windows (Mac: Vision): the page is drawn by Windows.Data.Pdf at
/// about 2000 pixels on the long side and read in the languages of the user's profile, or Italian or English.
/// Without an OCR language installed the pages wait in the queue.
/// </summary>
public sealed class WindowsPageReader : IPageReader
{
    private readonly Lazy<OcrEngine?> engine = new(() =>
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? (OcrEngine.IsLanguageSupported(new Language("it-IT")) ? OcrEngine.TryCreateFromLanguage(new Language("it-IT")) : null)
                ?? (OcrEngine.IsLanguageSupported(new Language("en-US")) ? OcrEngine.TryCreateFromLanguage(new Language("en-US")) : null);
        }
        catch (Exception e) { Log.Info($"ocr: non disponibile: {e.Message}"); return null; }
    });

    /// <summary>Reopening the same PDF for every page is costly.</summary>
    private (string path, PdfDoc doc)? open;

    public bool Available => engine.Value is not null;

    public async Task<string?> ReadPageAsync(string pdfPath, int page, CancellationToken ct)
    {
        if (engine.Value is not { } ocr) return null;
        if (open?.path != pdfPath)
        {
            var file = await StorageFile.GetFileFromPathAsync(pdfPath);
            open = (pdfPath, await PdfDoc.LoadFromFileAsync(file));
        }
        var doc = open.Value.doc;
        if (page < 1 || page > doc.PageCount) return null;
        using var p = doc.GetPage((uint)(page - 1));
        double w = p.Size.Width, h = p.Size.Height;
        if (w <= 0 || h <= 0) return null;
        double scale = Math.Min(2.5, Math.Min(2000, OcrEngine.MaxImageDimension) / Math.Max(w, h));
        using var stream = new InMemoryRandomAccessStream();
        await p.RenderToStreamAsync(stream, new PdfRenderOptions { DestinationWidth = (uint)(w * scale), DestinationHeight = (uint)(h * scale) });
        ct.ThrowIfCancellationRequested();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await ocr.RecognizeAsync(bitmap);
        var lines = result.Lines.Select(l => l.Text).Where(t => t.Length > 0).ToList();
        return lines.Count == 0 ? null : string.Join(" ", lines);
    }

    /// <summary>On battery the scanned pages and the vectors wait (Mac: the same rule).</summary>
    public static bool OnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
