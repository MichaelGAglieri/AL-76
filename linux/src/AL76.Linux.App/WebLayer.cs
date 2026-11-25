using System.Runtime.InteropServices;
using AL76.Linux.App.Native;
using AL76.Linux.Platform;

namespace AL76.Linux.App;

/// <summary>
/// What every window shares (Windows: WebLayer with WebView2): our pages from the app folder on the al76:// scheme
/// (al76://hud/… the sphere and nucleus pages of Windows, al76://app/… the Linux pages, al76://companion/… the
/// user's ROBOTTINO model), nothing else ever loads, and the same window.chrome.webview the Windows pages talk to.
/// </summary>
internal static class WebLayer
{
    public const string Scheme = "al76";
    private static bool registered;

    /// <summary>
    /// The Windows pages use WebView2's window.chrome.webview (postMessage, a "message" event with e.data). The same
    /// object on top of WebKit's message handler, so windows/web/hud runs unchanged.
    /// </summary>
    public const string Bridge = """
        (() => {
          const handler = window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.al76;
          const listeners = [];
          window.chrome = window.chrome || {};
          window.chrome.webview = {
            postMessage: (m) => handler && handler.postMessage(JSON.stringify(m)),
            addEventListener: (type, fn) => { if (type === 'message') listeners.push(fn); },
            removeEventListener: (type, fn) => { const i = listeners.indexOf(fn); if (i >= 0) listeners.splice(i, 1); },
            __deliver: (data) => { for (const fn of listeners.slice()) { try { fn({ data }); } catch (e) { console.error(e); } } },
          };
        })();
        """;

    /// <summary>The folders behind each host; a path that leaves its folder is refused.</summary>
    public static string? Resolve(string uri, string appFolder, string companion)
    {
        if (!uri.StartsWith(Scheme + "://", StringComparison.Ordinal)) return null;
        string rest = uri[(Scheme.Length + 3)..];
        int q = rest.IndexOfAny(['?', '#']);
        if (q >= 0) rest = rest[..q];
        int slash = rest.IndexOf('/');
        if (slash <= 0) return null;
        string host = rest[..slash], path = Uri.UnescapeDataString(rest[(slash + 1)..]);
        string? root = host switch
        {
            "hud" => Path.Combine(appFolder, "web", "hud"),
            "app" => Path.Combine(appFolder, "web", "app"),
            "companion" => companion,
            _ => null,
        };
        if (root is null || path.Length == 0) return null;
        string full = Path.GetFullPath(Path.Combine(root, path));
        string rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.Ordinal) ? full : null;
    }

    public static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html",
        ".js" or ".mjs" => "text/javascript",
        ".css" => "text/css",
        ".json" => "application/json",
        ".gltf" => "model/gltf+json",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".wasm" => "application/wasm",
        _ => "application/octet-stream",
    };

    [UnmanagedCallersOnly]
    private static void Serve(IntPtr request, IntPtr _)
    {
        string uri = Gtk.Utf8(Gtk.UriSchemeRequestGetUri(request));
        string? file = Resolve(uri, AppContext.BaseDirectory, XdgPaths.Companion);
        byte[]? bytes = null;
        if (file is not null)
        {
            try { bytes = File.ReadAllBytes(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (bytes is null)
        {
            var error = Gtk.ErrorNewLiteral(Gtk.QuarkFromString("al76"), 404, "not found");
            Gtk.UriSchemeRequestFinishError(request, error);
            Gtk.ErrorFree(error);
            return;
        }
        // GLib frees the copy with g_free when the page is done with it.
        IntPtr copy = Gtk.Malloc((nuint)Math.Max(1, bytes.Length));
        Marshal.Copy(bytes, 0, copy, bytes.Length);
        IntPtr stream = Gtk.MemoryInputStreamNewFromData(copy, bytes.Length, Gtk.GFree);
        Gtk.UriSchemeRequestFinish(request, stream, bytes.Length, Mime(file!));
        Gtk.ObjectUnref(stream);
    }

    /// <summary>Once, before the first web view: the al76:// scheme, secure and allowed to load modules and fetch.</summary>
    public static unsafe void Register()
    {
        if (registered) return;
        registered = true;
        IntPtr context = Gtk.WebContextGetDefault();
        Gtk.WebContextRegisterUriScheme(context, Scheme, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&Serve, IntPtr.Zero, IntPtr.Zero);
        IntPtr security = Gtk.WebContextGetSecurityManager(context);
        Gtk.SecurityRegisterSecure(security, Scheme);
        Gtk.SecurityRegisterCors(security, Scheme);
    }
}
