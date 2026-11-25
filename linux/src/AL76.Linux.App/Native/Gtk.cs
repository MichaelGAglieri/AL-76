using System.Runtime.InteropServices;

namespace AL76.Linux.App.Native;

/// <summary>
/// The few GTK3, GLib and WebKitGTK 4.1 functions AL-76 needs, called directly (no GtkSharp: unmaintained).
/// Every call happens on the GTK thread; <see cref="GtkLoop"/> brings work there.
/// </summary>
internal static partial class Gtk
{
    public const string LibGtk = "libgtk-3.so.0";
    public const string LibGdk = "libgdk-3.so.0";
    public const string LibGlib = "libglib-2.0.so.0";
    public const string LibGobject = "libgobject-2.0.so.0";
    public const string LibGio = "libgio-2.0.so.0";
    public const string LibWebKit = "libwebkit2gtk-4.1.so.0";
    public const string LibJsc = "libjavascriptcoregtk-4.1.so.0";
    public const string LibCairo = "libcairo.so.2";

    [StructLayout(LayoutKind.Sequential)]
    public struct GdkRgba { public double Red, Green, Blue, Alpha; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CairoRectangleInt { public int X, Y, Width, Height; }

    // GTK
    [LibraryImport(LibGtk, EntryPoint = "gtk_init_check")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitCheck(IntPtr argc, IntPtr argv);
    [LibraryImport(LibGtk, EntryPoint = "gtk_main")] public static partial void RunMain();
    [LibraryImport(LibGtk, EntryPoint = "gtk_main_quit")] public static partial void MainQuit();
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_new")] public static partial IntPtr WindowNew(int type);
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_set_title", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void WindowSetTitle(IntPtr window, string title);
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_set_default_size")] public static partial void WindowSetDefaultSize(IntPtr window, int w, int h);
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_resize")] public static partial void WindowResize(IntPtr window, int w, int h);
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_move")] public static partial void WindowMove(IntPtr window, int x, int y);
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_get_position")] public static partial void WindowGetPosition(IntPtr window, out int x, out int y);
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_set_decorated")] public static partial void WindowSetDecorated(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool on);
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_set_resizable")] public static partial void WindowSetResizable(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool on);
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_set_keep_above")] public static partial void WindowSetKeepAbove(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool on);
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_set_keep_below")] public static partial void WindowSetKeepBelow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool on);
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_set_skip_taskbar_hint")] public static partial void WindowSetSkipTaskbar(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool on);
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_set_skip_pager_hint")] public static partial void WindowSetSkipPager(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool on);
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_set_type_hint")] public static partial void WindowSetTypeHint(IntPtr window, int hint);
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_set_icon_name", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void WindowSetIconName(IntPtr window, string name);
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_present")] public static partial void WindowPresent(IntPtr window);
    [LibraryImport(LibGtk, EntryPoint = "gtk_window_begin_move_drag")] public static partial void WindowBeginMoveDrag(IntPtr window, int button, int rootX, int rootY, uint time);
    [LibraryImport(LibGtk, EntryPoint = "gtk_widget_set_app_paintable")] public static partial void WidgetSetAppPaintable(IntPtr widget, [MarshalAs(UnmanagedType.Bool)] bool on);
    [LibraryImport(LibGtk, EntryPoint = "gtk_widget_get_screen")] public static partial IntPtr WidgetGetScreen(IntPtr widget);
    [LibraryImport(LibGtk, EntryPoint = "gtk_widget_set_visual")] public static partial void WidgetSetVisual(IntPtr widget, IntPtr visual);
    [LibraryImport(LibGtk, EntryPoint = "gtk_widget_show_all")] public static partial void WidgetShowAll(IntPtr widget);
    [LibraryImport(LibGtk, EntryPoint = "gtk_widget_hide")] public static partial void WidgetHide(IntPtr widget);
    [LibraryImport(LibGtk, EntryPoint = "gtk_widget_get_visible")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WidgetGetVisible(IntPtr widget);
    [LibraryImport(LibGtk, EntryPoint = "gtk_widget_input_shape_combine_region")] public static partial void WidgetInputShape(IntPtr widget, IntPtr region);
    [LibraryImport(LibGtk, EntryPoint = "gtk_container_add")] public static partial void ContainerAdd(IntPtr container, IntPtr widget);
    [LibraryImport(LibGtk, EntryPoint = "gtk_get_current_event_time")] public static partial uint CurrentEventTime();

    // GDK
    [LibraryImport(LibGdk, EntryPoint = "gdk_screen_get_rgba_visual")] public static partial IntPtr ScreenGetRgbaVisual(IntPtr screen);
    [LibraryImport(LibGdk, EntryPoint = "gdk_screen_is_composited")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenIsComposited(IntPtr screen);
    [LibraryImport(LibGdk, EntryPoint = "gdk_display_get_default")] public static partial IntPtr DisplayGetDefault();
    [LibraryImport(LibGdk, EntryPoint = "gdk_display_get_default_seat")] public static partial IntPtr DisplayGetDefaultSeat(IntPtr display);
    [LibraryImport(LibGdk, EntryPoint = "gdk_seat_get_pointer")] public static partial IntPtr SeatGetPointer(IntPtr seat);
    [LibraryImport(LibGdk, EntryPoint = "gdk_device_get_position")] public static partial void DeviceGetPosition(IntPtr device, IntPtr screen, out int x, out int y);
    [LibraryImport(LibGdk, EntryPoint = "gdk_display_get_primary_monitor")] public static partial IntPtr DisplayGetPrimaryMonitor(IntPtr display);
    [LibraryImport(LibGdk, EntryPoint = "gdk_display_get_monitor")] public static partial IntPtr DisplayGetMonitor(IntPtr display, int n);
    [LibraryImport(LibGdk, EntryPoint = "gdk_monitor_get_workarea")] public static partial void MonitorGetWorkarea(IntPtr monitor, out CairoRectangleInt area);
    [LibraryImport(LibGdk, EntryPoint = "gdk_set_allowed_backends", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void SetAllowedBackends(string backends);

    // cairo
    [LibraryImport(LibCairo, EntryPoint = "cairo_set_source_rgba")] public static partial void CairoSetSourceRgba(IntPtr cr, double r, double g, double b, double a);
    [LibraryImport(LibCairo, EntryPoint = "cairo_set_operator")] public static partial void CairoSetOperator(IntPtr cr, int op);
    [LibraryImport(LibCairo, EntryPoint = "cairo_paint")] public static partial void CairoPaint(IntPtr cr);
    [LibraryImport(LibCairo, EntryPoint = "cairo_region_create_rectangle")] public static partial IntPtr CairoRegionCreateRectangle(ref CairoRectangleInt rect);
    [LibraryImport(LibCairo, EntryPoint = "cairo_region_destroy")] public static partial void CairoRegionDestroy(IntPtr region);

    // GLib / GObject / GIO
    [LibraryImport(LibGlib, EntryPoint = "g_idle_add")] public static partial uint IdleAdd(IntPtr function, IntPtr data);
    [LibraryImport(LibGlib, EntryPoint = "g_timeout_add")] public static partial uint TimeoutAdd(uint interval, IntPtr function, IntPtr data);
    [LibraryImport(LibGlib, EntryPoint = "g_malloc")] public static partial IntPtr Malloc(nuint size);
    [LibraryImport(LibGlib, EntryPoint = "g_free")] public static partial void Free(IntPtr mem);
    [LibraryImport(LibGlib, EntryPoint = "g_quark_from_string", StringMarshalling = StringMarshalling.Utf8)] public static partial uint QuarkFromString(string s);
    [LibraryImport(LibGlib, EntryPoint = "g_error_new_literal", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr ErrorNewLiteral(uint domain, int code, string message);
    [LibraryImport(LibGlib, EntryPoint = "g_error_free")] public static partial void ErrorFree(IntPtr error);
    [LibraryImport(LibGobject, EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
    public static partial ulong SignalConnect(IntPtr instance, string signal, IntPtr handler, IntPtr data, IntPtr destroy, int flags);
    [LibraryImport(LibGobject, EntryPoint = "g_object_unref")] public static partial void ObjectUnref(IntPtr obj);
    [LibraryImport(LibGio, EntryPoint = "g_memory_input_stream_new_from_data")]
    public static partial IntPtr MemoryInputStreamNewFromData(IntPtr data, nint length, IntPtr destroy);

    // WebKitGTK 4.1
    [LibraryImport(LibWebKit, EntryPoint = "webkit_web_context_get_default")] public static partial IntPtr WebContextGetDefault();
    [LibraryImport(LibWebKit, EntryPoint = "webkit_web_context_register_uri_scheme", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void WebContextRegisterUriScheme(IntPtr context, string scheme, IntPtr callback, IntPtr data, IntPtr destroy);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_web_context_get_security_manager")] public static partial IntPtr WebContextGetSecurityManager(IntPtr context);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_security_manager_register_uri_scheme_as_secure", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void SecurityRegisterSecure(IntPtr manager, string scheme);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_security_manager_register_uri_scheme_as_cors_enabled", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void SecurityRegisterCors(IntPtr manager, string scheme);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_user_content_manager_new")] public static partial IntPtr UserContentManagerNew();
    [LibraryImport(LibWebKit, EntryPoint = "webkit_user_content_manager_register_script_message_handler", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UserContentRegisterHandler(IntPtr manager, string name);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_user_content_manager_add_script")] public static partial void UserContentAddScript(IntPtr manager, IntPtr script);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_user_script_new", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr UserScriptNew(string source, int frames, int time, IntPtr allow, IntPtr block);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_user_script_unref")] public static partial void UserScriptUnref(IntPtr script);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_web_view_new_with_user_content_manager")] public static partial IntPtr WebViewNewWithManager(IntPtr manager);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_web_view_load_uri", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void WebViewLoadUri(IntPtr view, string uri);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_web_view_set_background_color")] public static partial void WebViewSetBackground(IntPtr view, ref GdkRgba color);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_web_view_get_settings")] public static partial IntPtr WebViewGetSettings(IntPtr view);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_web_view_evaluate_javascript", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void WebViewEvaluate(IntPtr view, string script, nint length, IntPtr world, IntPtr sourceUri, IntPtr cancellable, IntPtr callback, IntPtr data);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_settings_set_enable_developer_extras")] public static partial void SettingsDeveloperExtras(IntPtr settings, [MarshalAs(UnmanagedType.Bool)] bool on);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_settings_set_enable_webgl")] public static partial void SettingsWebGl(IntPtr settings, [MarshalAs(UnmanagedType.Bool)] bool on);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_settings_set_hardware_acceleration_policy")] public static partial void SettingsAcceleration(IntPtr settings, int policy);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_settings_set_enable_back_forward_navigation_gestures")] public static partial void SettingsGestures(IntPtr settings, [MarshalAs(UnmanagedType.Bool)] bool on);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_uri_scheme_request_get_uri")] public static partial IntPtr UriSchemeRequestGetUri(IntPtr request);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_uri_scheme_request_finish", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void UriSchemeRequestFinish(IntPtr request, IntPtr stream, long length, string mime);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_uri_scheme_request_finish_error")] public static partial void UriSchemeRequestFinishError(IntPtr request, IntPtr error);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_javascript_result_get_js_value")] public static partial IntPtr JavascriptResultGetValue(IntPtr result);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_navigation_policy_decision_get_navigation_action")] public static partial IntPtr NavigationDecisionGetAction(IntPtr decision);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_navigation_action_get_request")] public static partial IntPtr NavigationActionGetRequest(IntPtr action);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_uri_request_get_uri")] public static partial IntPtr UriRequestGetUri(IntPtr request);
    [LibraryImport(LibWebKit, EntryPoint = "webkit_policy_decision_ignore")] public static partial void PolicyIgnore(IntPtr decision);

    // JavaScriptCore
    [LibraryImport(LibJsc, EntryPoint = "jsc_value_to_string")] public static partial IntPtr JscValueToString(IntPtr value);

    /// <summary>A UTF-8 string GLib gave us; <paramref name="free"/> when we own it.</summary>
    public static string Utf8(IntPtr p, bool free = false)
    {
        if (p == IntPtr.Zero) return "";
        string s = Marshal.PtrToStringUTF8(p) ?? "";
        if (free) Free(p);
        return s;
    }

    public static readonly IntPtr GFree = NativeLibrary.GetExport(NativeLibrary.Load(LibGlib), "g_free");
}

internal static partial class Gdk
{
    [LibraryImport(Gtk.LibGdk, EntryPoint = "gdk_screen_get_default")] public static partial IntPtr ScreenGetDefault();
}
