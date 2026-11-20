using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AL76.Core.Diagnostics;
using AL76.Core.Tools;
using AL76.Platform;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AL76.App;

/// <summary>
/// webpage_to_pdf and the Recycle Bin for the document tools (Mac: WKWebView.pdf, trashItem). The page is
/// loaded in its own browser profile, separate from the one of the sphere and the nucleus: no cookies of
/// ours, nothing it runs can talk to AL-76. It is drawn in an invisible window and printed by WebView2.
/// </summary>
public sealed class WindowsDocuments : IDocumentPlatform
{
    private static Task<CoreWebView2Environment>? environment;

    public Task<(byte[] pdf, string title)?> WebPagePdfAsync(Uri url, CancellationToken ct) =>
        Application.Current.Dispatcher.Invoke(() => PrintAsync(url, ct));

    private static async Task<(byte[] pdf, string title)?> PrintAsync(Uri url, CancellationToken ct)
    {
        var window = new Window
        {
            Width = 1024, Height = 1400, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
            WindowStyle = WindowStyle.None, Title = "AL-76 PDF",
        };
        var web = new WebView2();
        window.Content = web;
        string file = Path.Combine(Path.GetTempPath(), $"al76-pagina-{Guid.NewGuid():N}.pdf");
        try
        {
            window.Show();
            environment ??= CoreWebView2Environment.CreateAsync(null, AppPaths.Browser + "-pagine");
            await web.EnsureCoreWebView2Async(await environment);
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            // Only web pages: no downloads, no other schemes.
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("http://") && !e.Uri.StartsWith("https://")) e.Cancel = true; };
            var loaded = new TaskCompletionSource<bool>();
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            core.Navigate(url.AbsoluteUri);
            var first = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(30), ct));
            if (first != loaded.Task || !loaded.Task.Result) return null;
            await Task.Delay(1000, ct);   // late images and fonts
            var settings = core.Environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            if (!await core.PrintToPdfAsync(file, settings)) return null;
            return (await File.ReadAllBytesAsync(file, ct), core.DocumentTitle ?? "");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Info($"pdf della pagina: {e.Message}");
            return null;
        }
        finally
        {
            web.Dispose();
            window.Close();
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    public bool Recycle(string path)
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Log.Info($"cestino: {e.Message}");
            return false;
        }
    }
}
