using System.Runtime.InteropServices;
using System.Text.Json;
using AL76.Core.Diagnostics;
using AL76.Linux.App.Native;

namespace AL76.Linux.App;

/// <summary>
/// A GTK window holding one of our pages. Transparent and frameless for the sphere and the nucleus, a normal
/// window for Settings and the status page. Messages: <see cref="Received"/> from the page (JSON),
/// <see cref="Post"/> to it.
/// </summary>
internal sealed class WebWindow
{
    public IntPtr Handle { get; }
    public IntPtr View { get; }
    private readonly GCHandle self;
    private readonly bool transparent;
    public event Action<JsonElement>? Received;
    /// <summary>The user closed the window: AL-76 hides it and keeps running (Windows: Closing → Cancel).</summary>
    public event Action? CloseRequested;

    public WebWindow(string title, int width, int height, bool transparent, bool decorated = true)
    {
        WebLayer.Register();
        this.transparent = transparent;
        self = GCHandle.Alloc(this);
        Handle = Gtk.WindowNew(0);
        Gtk.WindowSetTitle(Handle, title);
        Gtk.WindowSetIconName(Handle, "al76");
        Gtk.WindowSetDefaultSize(Handle, width, height);
        Gtk.WindowSetDecorated(Handle, decorated);
        if (transparent)
        {
            // Per-pixel transparency needs an RGBA visual and a compositor (GNOME, KDE, and Xwayland under them).
            IntPtr visual = Gtk.ScreenGetRgbaVisual(Gtk.WidgetGetScreen(Handle));
            if (visual != IntPtr.Zero) Gtk.WidgetSetVisual(Handle, visual);
            Gtk.WidgetSetAppPaintable(Handle, true);
            Connect(Handle, "draw", Callbacks.Draw);
        }
        Connect(Handle, "delete-event", Callbacks.Delete);

        IntPtr manager = Gtk.UserContentManagerNew();
        Gtk.UserContentRegisterHandler(manager, "al76");
        Connect(manager, "script-message-received::al76", Callbacks.Message);
        // At document start, top frame only: the bridge exists before the page's own scripts run.
        IntPtr script = Gtk.UserScriptNew(WebLayer.Bridge, 1, 0, IntPtr.Zero, IntPtr.Zero);
        Gtk.UserContentAddScript(manager, script);
        Gtk.UserScriptUnref(script);

        View = Gtk.WebViewNewWithManager(manager);
        IntPtr settings = Gtk.WebViewGetSettings(View);
        Gtk.SettingsWebGl(settings, true);
        Gtk.SettingsGestures(settings, false);
        Gtk.SettingsDeveloperExtras(settings, File.Exists(Path.Combine(AppContext.BaseDirectory, "dev.flag")));
        if (transparent)
        {
            var clear = new Gtk.GdkRgba();
            Gtk.WebViewSetBackground(View, ref clear);
        }
        Connect(View, "decide-policy", Callbacks.Policy);
        Gtk.ContainerAdd(Handle, View);
    }

    private unsafe void Connect(IntPtr instance, string signal, IntPtr handler) =>
        Gtk.SignalConnect(instance, signal, handler, GCHandle.ToIntPtr(self), IntPtr.Zero, 0);

    public void Load(string uri) => Gtk.WebViewLoadUri(View, uri);
    private (int x, int y)? wanted;

    public void Show()
    {
        Gtk.WidgetShowAll(Handle);
        Gtk.WindowPresent(Handle);
        // Mutter places a new window itself and ignores a position given before it is mapped (seen on GNOME 50 with
        // Xwayland): the place is set again once the window is on screen.
        if (wanted is { } p)
        {
            Gtk.WindowMove(Handle, p.x, p.y);
            _ = Task.Delay(150).ContinueWith(_ => GtkLoop.Run(() => Gtk.WindowMove(Handle, p.x, p.y)));
        }
    }
    public void Hide() => Gtk.WidgetHide(Handle);
    public bool Visible => Gtk.WidgetGetVisible(Handle);
    public void Move(int x, int y) { wanted = (x, y); Gtk.WindowMove(Handle, x, y); }
    public (int x, int y) Position { get { Gtk.WindowGetPosition(Handle, out int x, out int y); return (x, y); } }

    /// <summary>Floats above the other windows, out of the taskbar and the window switcher (sphere, satellite, robot).</summary>
    public void Float(bool above)
    {
        Gtk.WindowSetKeepAbove(Handle, above);
        Gtk.WindowSetSkipTaskbar(Handle, true);
        Gtk.WindowSetSkipPager(Handle, true);
        Gtk.WindowSetResizable(Handle, false);
        // GDK_WINDOW_TYPE_HINT_UTILITY: no taskbar entry, the window manager still lets it take clicks and keys.
        Gtk.WindowSetTypeHint(Handle, 5);
    }

    /// <summary>Starts moving the window with the mouse, from where the pointer is now (the page saw a drag).</summary>
    public void BeginDrag()
    {
        IntPtr pointer = Gtk.SeatGetPointer(Gtk.DisplayGetDefaultSeat(Gtk.DisplayGetDefault()));
        Gtk.DeviceGetPosition(pointer, IntPtr.Zero, out int x, out int y);
        wanted = null;   // the user places it from now on
        Gtk.WindowBeginMoveDrag(Handle, 1, x, y, Gtk.CurrentEventTime());
    }

    /// <summary>Sends a JSON message to the page (Windows: PostWebMessageAsJson).</summary>
    public void Post(object message) => PostJson(JsonSerializer.Serialize(message));

    public void PostJson(string json) =>
        GtkLoop.Run(() => Gtk.WebViewEvaluate(View, $"window.chrome && window.chrome.webview && window.chrome.webview.__deliver({json});",
            -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));

    private void OnMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            Received?.Invoke(doc.RootElement.Clone());
        }
        catch (JsonException e) { Log.Info($"pagina: messaggio non valido ({e.Message})"); }
    }

    private static WebWindow From(IntPtr data) => (WebWindow)GCHandle.FromIntPtr(data).Target!;

    /// <summary>The native signal handlers: plain static functions, so nothing is collected while GTK holds them.</summary>
    private static unsafe class Callbacks
    {
        public static readonly IntPtr Draw = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&OnDraw;
        public static readonly IntPtr Delete = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&OnDelete;
        public static readonly IntPtr Message = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnScriptMessage;
        public static readonly IntPtr Policy = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int, IntPtr, int>)&OnPolicy;

        [UnmanagedCallersOnly]
        private static int OnDraw(IntPtr widget, IntPtr cr, IntPtr data)
        {
            // Clears the window to fully transparent; the web view paints on top.
            Gtk.CairoSetSourceRgba(cr, 0, 0, 0, 0);
            Gtk.CairoSetOperator(cr, 1);   // CAIRO_OPERATOR_SOURCE
            Gtk.CairoPaint(cr);
            return 0;
        }

        [UnmanagedCallersOnly]
        private static int OnDelete(IntPtr widget, IntPtr ev, IntPtr data)
        {
            var w = From(data);
            if (w.CloseRequested is null) return 0;
            try { w.CloseRequested(); } catch (Exception e) { Log.Info($"errore alla chiusura: {e}"); }
            return 1;   // handled: the window stays (hidden by the handler)
        }

        [UnmanagedCallersOnly]
        private static void OnScriptMessage(IntPtr manager, IntPtr result, IntPtr data)
        {
            // WebKitGTK 4.1 hands a WebKitJavascriptResult; the value inside is the JSON string the bridge made.
            string json = Gtk.Utf8(Gtk.JscValueToString(Gtk.JavascriptResultGetValue(result)), free: true);
            try { From(data).OnMessage(json); } catch (Exception e) { Log.Info($"errore nel messaggio della pagina: {e}"); }
        }

        [UnmanagedCallersOnly]
        private static int OnPolicy(IntPtr view, IntPtr decision, int type, IntPtr data)
        {
            // 0 navigation, 1 new window, 2 response. Only our own pages; no new windows ever.
            if (type == 1) { Gtk.PolicyIgnore(decision); return 1; }
            if (type == 0)
            {
                string uri = Gtk.Utf8(Gtk.UriRequestGetUri(Gtk.NavigationActionGetRequest(Gtk.NavigationDecisionGetAction(decision))));
                if (!uri.StartsWith(WebLayer.Scheme + "://", StringComparison.Ordinal)) { Gtk.PolicyIgnore(decision); return 1; }
            }
            return 0;
        }
    }
}
