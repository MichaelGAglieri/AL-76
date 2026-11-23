using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AL76.Core.Voice;
using AL76.Platform;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Forms = System.Windows.Forms;

namespace AL76.App;

/// <summary>
/// What the sphere and the nucleus share: one WebView2 browser for both, pages served from web/hud on a
/// virtual host, transparent windows without frame, and the screen in the windows' units.
/// </summary>
public static class WebLayer
{
    public const string Host = "hud.al76";
    /// <summary>The user's ROBOTTINO model, read by the page from its own host (never copied into AL-76).</summary>
    public const string CompanionHost = "companion.al76";

    private static Task<CoreWebView2Environment>? environment;

    /// <summary>One browser environment for every window: our own pages only. On PCs without a usable GPU (the VM)
    /// WebGL needs the software renderer.</summary>
    public static Task<CoreWebView2Environment> Environment() => environment ??=
        CoreWebView2Environment.CreateAsync(null, AppPaths.Browser, new CoreWebView2EnvironmentOptions("--enable-unsafe-swiftshader"));

    /// <summary>Prepares a WebView2 for one of our pages: no browser menus or keys, nothing else ever opens.</summary>
    public static async Task<CoreWebView2> StartAsync(WebView2 web, Action<string> received)
    {
        await web.EnsureCoreWebView2Async(await Environment());
        var core = web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = File.Exists(Path.Combine(AppContext.BaseDirectory, "dev.flag"));
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppContext.BaseDirectory, "web", "hud"), CoreWebView2HostResourceAccessKind.DenyCors);
        // Always mapped: a model installed from the settings shows without a restart.
        try { Directory.CreateDirectory(AppPaths.Companion); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (Directory.Exists(AppPaths.Companion))
            core.SetVirtualHostNameToFolderMapping(CompanionHost, AppPaths.Companion, CoreWebView2HostResourceAccessKind.Allow);
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith($"https://{Host}/")) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += (_, e) => received(e.WebMessageAsJson);
        return core;
    }

    /// <summary>Per-pixel transparency for a normal window: blur-behind with an empty region (what Tauri does for
    /// WebView2), and no Windows 11 rounded corners or border around the invisible rectangle.</summary>
    public static void MakeTransparent(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;
        var region = CreateRectRgn(0, 0, -1, -1);
        var blur = new DwmBlurBehind { Flags = 0x3, Enable = true, RegionBlur = region };
        DwmEnableBlurBehindWindow(hwnd, ref blur);
        DeleteObject(region);
        int round = 1, none = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));   // DWMWA_WINDOW_CORNER_PREFERENCE = DONOTROUND
        DwmSetWindowAttribute(hwnd, 34, ref none, sizeof(int));    // DWMWA_BORDER_COLOR = none
    }

    /// <summary>Windows moves the window with the mouse, as if the page were a title bar.</summary>
    public static void SystemDrag(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        ReleaseCapture();
        SendMessage(hwnd, 0x00A1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
    }

    /// <summary>The working area (without the taskbar) of the screen that holds the point, in DIPs.</summary>
    public static Area WorkArea(Window window, Point at)
    {
        var scale = Scale(window);
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)(at.X * scale.X), (int)(at.Y * scale.Y)));
        var r = screen.WorkingArea;
        return new Area(r.Left / scale.X, r.Top / scale.Y, r.Width / scale.X, r.Height / scale.Y);
    }

    /// <summary>The mouse pointer, in DIPs.</summary>
    public static Point Cursor(Window window)
    {
        GetCursorPos(out var p);
        var scale = Scale(window);
        return new Point(p.X / scale.X, p.Y / scale.Y);
    }

    /// <summary>Device pixels per DIP for the window.</summary>
    public static Vector Scale(Window window)
    {
        if (PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice is { } m && m.M11 > 0 && m.M22 > 0)
            return new Vector(m.M11, m.M22);
        // A window never shown yet: the primary screen's scale.
        double k = Forms.Screen.PrimaryScreen is { } p && SystemParameters.PrimaryScreenWidth > 0 ? p.Bounds.Width / SystemParameters.PrimaryScreenWidth : 1;
        return new Vector(k, k);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind { public int Flags; public bool Enable; public IntPtr RegionBlur; public bool TransitionOnMaximized; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blur);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
}
