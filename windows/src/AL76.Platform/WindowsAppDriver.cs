using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using AL76.Core.Diagnostics;
using AL76.Core.Tools;
using AL76.Core.Voice;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AL76.Platform;

/// <summary>
/// App control on Windows (Mac: the AX side of AppControl): UI Automation reads and presses the elements of other
/// apps' windows, SendInput types and presses keys, PrintWindow and the screen take pictures. The rules are all in
/// <see cref="AppControl"/>; here only how. AL-76's own windows are never targets and never in a picture.
/// <para>Everything runs on a pool thread aware of each monitor's DPI, so rectangles, points and pictures are in the
/// same physical pixels.</para>
/// </summary>
public sealed partial class WindowsAppDriver : IAppDriver
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "librewolf", "waterfox" };

    /// <summary>The long side of a picture: what the models read best.</summary>
    private const int MaxSide = 1568;
    private const int MaxNodes = 400;

    // ---- windows ----

    public AppWindow? Find(string name)
    {
        var windows = TopWindows();
        var labels = windows.SelectMany(w => new[] { w.Name, w.Process, w.Title }).Where(l => l.Length > 0);
        if (WindowsPcActions.Best(name, labels) is not string best) return null;
        // The front-most window of that app (the list is in z-order).
        return windows.FirstOrDefault(w => w.Name == best || w.Process == best) ?? windows.FirstOrDefault(w => w.Title == best);
    }

    /// <summary>Visible top-level windows of other processes, front first: no tool windows, no owned popups, no cloaked ones.</summary>
    private static List<AppWindow> TopWindows()
    {
        var list = new List<AppWindow>();
        var labels = new Dictionary<int, (string name, string process)>();
        int me = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            if (((long)GetWindowLongPtr(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            string title = Title(h);
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == me || pid == 0) return true;
            if (!labels.TryGetValue((int)pid, out var l)) labels[(int)pid] = l = Describe((int)pid);
            if (l.process.Length == 0) return true;
            // Store apps live in ApplicationFrameHost: their name is the title (Calcolatrice, Impostazioni).
            string appName = l.process.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ? title : l.name;
            list.Add(new AppWindow(appName, l.process, title, (int)pid, (long)h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static (string name, string process) Describe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string process = p.ProcessName;
            string name = process;
            try { if (p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d) name = d.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? d[..^4] : d; }
            catch (Exception) { }   // elevated processes do not show their module
            return (name, process);
        }
        catch (Exception) { return ("", ""); }
    }

    private static string Title(IntPtr h)
    {
        int n = GetWindowTextLength(h);
        if (n <= 0) return "";
        var buffer = new char[n + 1];
        int got = GetWindowText(h, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, got));
    }

    private static bool IsBrowser(AppWindow w) => Browsers.Contains(w.Process);

    // ---- reading ----

    public Task<UiSnapshot> ReadAsync(AppWindow window, bool menus, CancellationToken ct) => Run(() => Read(window, menus, ct), ct);

    private static UiSnapshot Read(AppWindow w, bool menus, CancellationToken ct)
    {
        AutomationElement root;
        try { root = AutomationElement.FromHandle((IntPtr)w.Handle); }
        catch (Exception e) when (e is ElementNotAvailableException or ArgumentException or InvalidOperationException) { return new UiSnapshot([]); }
        var nodes = new List<UiNode>();
        string url = "";
        bool browser = IsBrowser(w);
        var walker = TreeWalker.ControlViewWalker;
        var clock = Stopwatch.StartNew();

        void Visit(AutomationElement e, int depth)
        {
            if (nodes.Count >= MaxNodes || clock.ElapsedMilliseconds > 4000 || depth > 40 || ct.IsCancellationRequested) return;
            AutomationElement.AutomationElementInformation info;
            try { info = e.Current; }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { return; }
            var type = info.ControlType;
            if (depth > 0 && info.IsOffscreen) return;   // only what is on screen, like the Mac
            if (!menus && (type == ControlType.MenuBar || type == ControlType.TitleBar)) return;
            if (type == ControlType.ScrollBar) return;   // its arrows and pages are noise: app_scroll does that
            var node = Node(e, info, browser);
            if (node is not null)
            {
                if (browser && url.Length == 0 && node.IsField && node.Value is { } v && LooksLikeAddress(v)) url = v;
                if (!(node.IsText && nodes.Count > 0 && nodes[^1].Label == node.Label)) nodes.Add(node);
                if (node.Kind is not ("item" or "tab" or "popup")) return;   // a button's or a link's inside is its label
            }
            try
            {
                for (var c = walker.GetFirstChild(e); c is not null; c = walker.GetNextSibling(c)) Visit(c, depth + 1);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
        }

        Visit(root, 0);
        return new UiSnapshot(nodes, url);
    }

    private static bool LooksLikeAddress(string v) =>
        v.Length is > 3 and < 2048 && !v.Contains(' ') && (v.Contains("://") || v.Contains('.'));

    private static UiNode? Node(AutomationElement e, AutomationElement.AutomationElementInformation info, bool browser)
    {
        var type = info.ControlType;
        string label = Clean(info.Name);
        if (label.Length == 0) label = Clean(info.HelpText);
        string? kind =
            type == ControlType.Button || type == ControlType.SplitButton ? "button" :
            type == ControlType.Hyperlink ? "link" :
            type == ControlType.Edit ? "field" :
            type == ControlType.Document ? (Writable(e, info) ? "field" : null) :
            type == ControlType.CheckBox ? "checkbox" :
            type == ControlType.RadioButton ? "radio" :
            type == ControlType.TabItem ? "tab" :
            type == ControlType.MenuItem ? "menu item" :
            type == ControlType.ComboBox ? "popup" :
            type == ControlType.ListItem || type == ControlType.TreeItem || type == ControlType.DataItem ? "item" :
            type == ControlType.Text ? "text" : null;
        if (kind is null) return null;
        if (kind == "text") return label.Length == 0 ? null : new UiNode("text", Clip(label, 300));
        // A button without a name says nothing to the model.
        if (label.Length == 0 && kind is "button" or "link" or "menu item" or "item" or "tab") label = Clean(info.AutomationId);
        if (label.Length == 0 && kind is not ("field" or "popup" or "checkbox")) return null;
        string? value = null;
        try
        {
            if (kind is "field" or "popup" && !info.IsPassword && e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                value = Clip(((ValuePattern)vp).Current.Value ?? "", 200);
            else if (kind == "checkbox" && e.TryGetCurrentPattern(TogglePattern.Pattern, out var tp))
                value = ((TogglePattern)tp).Current.ToggleState == ToggleState.On ? "on" : "off";
            else if (kind is "radio" or "tab" or "item" && e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp) && ((SelectionItemPattern)sp).Current.IsSelected)
                value = "selected";
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
        if (value is { Length: 0 }) value = null;
        string id = info.AutomationId ?? "";
        bool search = kind == "field" && (Has(label, "search", "cerca", "ricerca") || Has(id, "search", "cerca"));
        return new UiNode(kind, Clip(label, 120), value, info.IsEnabled, info.IsPassword, search, e);
    }

    /// <summary>A document that takes typing (Notepad, a mail body), not a web page or a read-only viewer.</summary>
    private static bool Writable(AutomationElement e, AutomationElement.AutomationElementInformation info)
    {
        try
        {
            if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) return !((ValuePattern)vp).Current.IsReadOnly;
            return info.IsKeyboardFocusable && e.TryGetCurrentPattern(TextPattern.Pattern, out _) && TreeWalker.ControlViewWalker.GetFirstChild(e) is null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { return false; }
    }

    private static bool Has(string text, params string[] words) => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    private static string Clean(string? s) => string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---- acting ----

    public Task<bool> PressAsync(AppWindow window, UiNode node, CancellationToken ct) => Run(() =>
    {
        if (node.Handle is not AutomationElement e) return false;
        try
        {
            Action? act = null;
            if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) act = ((InvokePattern)ip).Invoke;
            else if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) act = ((TogglePattern)tp).Toggle;
            else if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) act = ((SelectionItemPattern)sp).Select;
            else if (e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep))
            {
                var pattern = (ExpandCollapsePattern)ep;
                act = pattern.Current.ExpandCollapseState == ExpandCollapseState.Collapsed ? pattern.Expand : pattern.Collapse;
            }
            if (act is not null)
            {
                // A classic button that opens a modal dialog does not return from Invoke until it closes: do not wait for it.
                var t = Task.Run(act);
                if (t.Wait(2000) && t.IsFaulted) return false;
                return true;
            }
            // No pattern: a real click on its clickable point, with the window in front.
            if (!e.TryGetClickablePoint(out var pt)) return false;
            if (!Front((IntPtr)window.Handle)) return false;
            if (Covered((IntPtr)window.Handle, (int)pt.X, (int)pt.Y)) return false;
            Click((int)pt.X, (int)pt.Y);
            return true;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException or COMException or AggregateException)
        {
            Log.Info($"app: pressione non riuscita: {ex.Message}");
            return false;
        }
    }, ct);

    public Task<string?> TypeAsync(AppWindow window, UiNode field, string text, bool submit, CancellationToken ct) => Run<string?>(() =>
    {
        if (field.Handle is not AutomationElement e) return "The field is gone: read the app again.";
        var h = (IntPtr)window.Handle;
        try
        {
            var info = e.Current;
            if (info.IsPassword || field.Password) return "That is a password field: AL-76 never types there.";
            if (!info.IsEnabled) return "The field is disabled.";
            // AL-76 (the writing bar, the nucleus) gives the keyboard back to the app first.
            if (!Front(h)) return "Could not bring the app in front: the user may be using another window.";
            e.SetFocus();
            Thread.Sleep(80);
            if (!HasFocus(e)) return "Could not put the cursor in that field.";
            bool document = info.ControlType == ControlType.Document;
            if (!document)
            {
                // A single-line field: its text is replaced, like setting it on the Mac.
                string current = "";
                try { if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) current = ((ValuePattern)vp).Current.Value ?? ""; } catch (Exception) { }
                text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
                // Set straight through UI Automation when the field allows it: no keystrokes to lose.
                if (SetValue(e, text))
                {
                    if (!submit) return null;
                    Thread.Sleep(60);
                    if (Root(GetForegroundWindow()) != h) return "Typed, but another window came in front before Enter.";
                    Keys(new KeyCombo("enter", false, false, false, false));
                    return null;
                }
                if (current.Length > 0) Keys(new KeyCombo("a", true, false, false, false));
            }
            int sent = 0;
            foreach (var chunk in text.Replace("\r\n", "\n").Chunk(16))
            {
                if (ct.IsCancellationRequested) return $"Stopped by the user after {sent} characters.";
                // The user clicked elsewhere: never type into a window they did not expect.
                if (Root(GetForegroundWindow()) != h) return $"Stopped: another window came in front after {sent} characters.";
                foreach (char ch in chunk)
                {
                    if (ch == '\n') Keys(new KeyCombo("enter", false, false, false, false));
                    else Unicode(ch);
                    // A burst gets mixed up on a busy PC (repeated letters, lost spaces): one character at a time.
                    Thread.Sleep(12);
                }
                sent += chunk.Length;
            }
            if (submit)
            {
                Thread.Sleep(60);
                if (Root(GetForegroundWindow()) != h) return "Typed, but another window came in front before Enter.";
                Keys(new KeyCombo("enter", false, false, false, false));
            }
            return null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException or COMException)
        {
            return $"Could not type there: {ex.Message}";
        }
    }, ct);

    private static bool HasFocus(AutomationElement field)
    {
        try
        {
            if (field.Current.HasKeyboardFocus) return true;
            var focused = AutomationElement.FocusedElement;
            if (focused is null) return false;
            if (Automation.Compare(focused, field)) return true;
            // The focus can land on an inner part of the field (a rich editor's text area).
            for (var p = TreeWalker.RawViewWalker.GetParent(focused); p is not null; p = TreeWalker.RawViewWalker.GetParent(p))
                if (Automation.Compare(p, field)) return true;
            return false;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { return false; }
    }

    public Task<string?> KeyAsync(AppWindow window, KeyCombo key, CancellationToken ct) => Run<string?>(() =>
    {
        if (key.Win) return "The Windows key is not allowed.";
        if (!Front((IntPtr)window.Handle)) return "Could not bring the app in front.";
        Keys(key);
        return null;
    }, ct);

    public Task<double?> ScrollAsync(AppWindow window, UiNode? inside, string direction, int pages, CancellationToken ct) => Run<double?>(() =>
    {
        var h = (IntPtr)window.Handle;
        pages = Math.Clamp(pages, 1, 4);
        AutomationElement? area = null;
        try
        {
            if (inside?.Handle is AutomationElement start)
                for (var p = start; p is not null && area is null; p = TreeWalker.ControlViewWalker.GetParent(p))
                    if (Scrollable(p)) area = p;
            area ??= LargestScrollable(AutomationElement.FromHandle(h));
            if (area is not null && area.TryGetCurrentPattern(ScrollPattern.Pattern, out var sp))
            {
                var s = (ScrollPattern)sp;
                double view = s.Current.VerticalViewSize, now = s.Current.VerticalScrollPercent;
                double step = view >= 100 ? 0 : view * 100 / (100 - view) * pages / 4;
                double target = direction switch
                {
                    "top" => 0,
                    "bottom" => 100,
                    "up" => Math.Max(0, now - step),
                    _ => Math.Min(100, now + step),
                };
                s.SetScrollPercent(ScrollPattern.NoScroll, target);
                Thread.Sleep(120);
                return s.Current.VerticalScrollPercent / 100;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException) { }
        // No scroll pattern (some web pages, games): the mouse wheel over the window, then the pointer goes back.
        if (!Front(h) || !GetWindowRect(h, out var r)) return null;
        int notches = direction switch { "top" or "bottom" => 40, _ => pages * 3 };
        int sign = direction is "up" or "top" ? 1 : -1;
        GetCursorPos(out var was);
        SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        for (int i = 0; i < notches; i++) { Wheel(sign * 120); Thread.Sleep(8); }
        SetCursorPos(was.X, was.Y);
        return null;
    }, ct);

    private static bool Scrollable(AutomationElement e)
    {
        try { return e.TryGetCurrentPattern(ScrollPattern.Pattern, out var sp) && ((ScrollPattern)sp).Current.VerticallyScrollable; }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { return false; }
    }

    private static AutomationElement? LargestScrollable(AutomationElement root)
    {
        AutomationElement? best = null;
        double bestArea = 0;
        var queue = new Queue<(AutomationElement e, int depth)>([(root, 0)]);
        int seen = 0;
        while (queue.Count > 0 && seen++ < 600)
        {
            var (e, depth) = queue.Dequeue();
            try
            {
                if (Scrollable(e))
                {
                    var b = e.Current.BoundingRectangle;
                    if (!b.IsEmpty && b.Width * b.Height > bestArea) { best = e; bestArea = b.Width * b.Height; }
                }
                if (depth < 12)
                    for (var c = TreeWalker.ControlViewWalker.GetFirstChild(e); c is not null; c = TreeWalker.ControlViewWalker.GetNextSibling(c))
                        queue.Enqueue((c, depth + 1));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
        }
        return best;
    }

    public Task<string?> ClickAsync(AppWindow window, Capture shot, double screenX, double screenY, CancellationToken ct) => Run<string?>(() =>
    {
        var h = (IntPtr)window.Handle;
        if (!IsWindow(h)) return "The window is closed.";
        if (IsIconic(h)) return "The window is minimized: look at it again with app_look.";
        var now = Frame(h);
        if (Math.Abs(now.Left - shot.Area.Left) > 2 || Math.Abs(now.Top - shot.Area.Top) > 2
            || Math.Abs(now.Right - now.Left - shot.Area.Width) > 2 || Math.Abs(now.Bottom - now.Top - shot.Area.Height) > 2)
            return "The window moved or changed size since the picture: look again with app_look.";
        if (!Front(h)) return "Could not bring the app in front.";
        int x = (int)Math.Round(screenX), y = (int)Math.Round(screenY);
        if (Covered(h, x, y)) return "Another window covers that point: look again with app_look.";
        Click(x, y);
        return null;
    }, ct);

    /// <summary>Whether the point belongs to another window (a popup of the same app counts as its own).</summary>
    private static bool Covered(IntPtr h, int x, int y)
    {
        var at = WindowFromPoint(new POINT { X = x, Y = y });
        if (at == IntPtr.Zero) return true;
        var root = Root(at);
        if (root == h) return false;
        GetWindowThreadProcessId(root, out uint a);
        GetWindowThreadProcessId(h, out uint b);
        return a != b || GetWindow(root, GW_OWNER) != h;
    }

    // ---- tabs ----

    public Task<int> CloseBrowserTabsAsync(string words, CancellationToken ct) => Run(() =>
    {
        string needle = TextTools.NormalizePhrase(words);
        if (needle.Length == 0) return 0;
        int closed = 0;
        foreach (var w in TopWindows().Where(IsBrowser))
        {
            if (ct.IsCancellationRequested || closed >= 10) break;
            AutomationElementCollection tabs;
            try
            {
                tabs = AutomationElement.FromHandle((IntPtr)w.Handle)
                    .FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { continue; }
            foreach (AutomationElement tab in tabs)
            {
                if (closed >= 10) break;
                try
                {
                    if (!(" " + TextTools.NormalizePhrase(tab.Current.Name) + " ").Contains(" " + needle + " ")) continue;
                    // The tab's own close button (Chrome, Edge, Firefox); otherwise select it and Ctrl+W.
                    var buttons = tab.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    if (buttons.Count > 0 && buttons[buttons.Count - 1].TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
                        ((InvokePattern)ip).Invoke();
                    else if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp) && Front((IntPtr)w.Handle))
                    {
                        ((SelectionItemPattern)sp).Select();
                        Thread.Sleep(150);
                        Keys(new KeyCombo("w", true, false, false, false));
                    }
                    else continue;
                    closed++;
                    Thread.Sleep(150);
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
            }
        }
        return closed;
    }, ct);

    // ---- pictures ----

    public async Task<Capture?> LookAsync(AppWindow window, CancellationToken ct)
    {
        var raw = await Run(() => WindowPixels((IntPtr)window.Handle), ct);
        return raw is null ? null : await Picture(raw.Value, window.Title, window.Handle);
    }

    public async Task<Capture?> ScreenAsync(bool wholeScreen, CancellationToken ct)
    {
        if (wholeScreen)
        {
            var screen = await Run(ScreenPixels, ct);
            return screen is null ? null : await Picture(screen.Value, "screen", 0);
        }
        // The front window that is not AL-76 (the list leaves AL-76 out and is in z-order).
        var front = TopWindows().FirstOrDefault(w => !IsIconic((IntPtr)w.Handle));
        if (front is null) return null;
        var raw = await Run(() => WindowPixels((IntPtr)front.Handle), ct);
        return raw is null ? null : await Picture(raw.Value, $"{front.Name} — {front.Title}", front.Handle);
    }

    private readonly record struct Pixels(byte[] Bgra, int Width, int Height, Area Area);

    /// <summary>The window as it draws itself (even if partly covered), cut to its visible frame.</summary>
    private static Pixels? WindowPixels(IntPtr h)
    {
        if (!IsWindow(h)) return null;
        if (IsIconic(h)) { ShowWindow(h, SW_SHOWNOACTIVATE); Thread.Sleep(300); }
        if (!GetWindowRect(h, out var wr)) return null;
        int w = wr.Right - wr.Left, ht = wr.Bottom - wr.Top;
        if (w <= 0 || ht <= 0 || w > 16384 || ht > 16384) return null;
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, ht);
        try
        {
            var old = SelectObject(mem, bmp);
            bool ok = PrintWindow(h, mem, PW_RENDERFULLCONTENT);
            SelectObject(mem, old);
            if (!ok) return null;
            var all = Bits(screen, bmp, w, ht);
            // The window rectangle includes the invisible resize borders: keep only the frame DWM draws.
            var fr = Frame(h);
            int left = Math.Clamp(fr.Left - wr.Left, 0, w - 1), top = Math.Clamp(fr.Top - wr.Top, 0, ht - 1);
            int cw = Math.Clamp(fr.Right - fr.Left, 1, w - left), ch = Math.Clamp(fr.Bottom - fr.Top, 1, ht - top);
            var cut = new byte[cw * ch * 4];
            for (int row = 0; row < ch; row++) System.Buffer.BlockCopy(all, ((top + row) * w + left) * 4, cut, row * cw * 4, cw * 4);
            return new Pixels(cut, cw, ch, new Area(wr.Left + left, wr.Top + top, cw, ch));
        }
        finally { DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
    }

    /// <summary>All monitors, with AL-76's own windows left out of the picture for that moment.</summary>
    private static Pixels? ScreenPixels() => ScreenPixels(hideOwn: true);

    private static Pixels? ScreenPixels(bool hideOwn)
    {
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN), h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0) return null;
        var mine = hideOwn ? OwnWindows() : [];
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

    private static List<IntPtr> OwnWindows()
    {
        var list = new List<IntPtr>();
        int me = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == me && IsWindowVisible(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static byte[] Bits(IntPtr dc, IntPtr bmp, int w, int h)
    {
        var info = new BITMAPINFO
        {
            Header = new BITMAPINFOHEADER
            {
                Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), Width = w, Height = -h,   // negative: rows top-down
                Planes = 1, BitCount = 32, Compression = 0,
            },
        };
        var bytes = new byte[w * h * 4];
        GetDIBits(dc, bmp, 0, (uint)h, bytes, ref info, 0);
        return bytes;
    }

    /// <summary>JPEG at most <see cref="MaxSide"/> on the long side; Width and Height are the picture's.</summary>
    private static async Task<Capture?> Picture(Pixels p, string label, long window)
    {
        double scale = Math.Min(1.0, MaxSide / (double)Math.Max(p.Width, p.Height));
        uint w = (uint)Math.Max(1, Math.Round(p.Width * scale)), h = (uint)Math.Max(1, Math.Round(p.Height * scale));
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)p.Width, (uint)p.Height, 96, 96, p.Bgra);
            encoder.BitmapTransform.ScaledWidth = w;
            encoder.BitmapTransform.ScaledHeight = h;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await encoder.FlushAsync();
            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return new Capture(bytes, (int)w, (int)h, label, window, p.Area);
        }
        catch (Exception e)
        {
            Log.Info($"app: immagine non codificata: {e.Message}");
            return null;
        }
    }

    // ---- input ----

    /// <summary>Brings the window in front. AL-76 is often the foreground app (the writing bar): joining its input to the
    /// front thread lets Windows move the focus, so AL-76 gives the keyboard back.</summary>
    private static bool Front(IntPtr h)
    {
        if (!IsWindow(h)) return false;
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        if (Root(GetForegroundWindow()) == h) return true;
        uint me = GetCurrentThreadId();
        uint front = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool joined = front != 0 && front != me && AttachThreadInput(me, front, true);
        try
        {
            BringWindowToTop(h);
            SetForegroundWindow(h);
        }
        finally { if (joined) AttachThreadInput(me, front, false); }
        if (WaitFront(h, 8)) return true;
        // Windows lets a process change the foreground only if it sent the last input: a lone Alt tap makes it ours.
        Send([Key(VK_MENU, 0, 0), Key(VK_MENU, 0, KEYEVENTF_KEYUP)]);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        return WaitFront(h, 16);
    }

    private static bool WaitFront(IntPtr h, int tries)
    {
        for (int i = 0; i < tries; i++)
        {
            if (Root(GetForegroundWindow()) == h) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static IntPtr Root(IntPtr h) => h == IntPtr.Zero ? h : GetAncestor(h, GA_ROOT);

    private static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(30);
        Send([Mouse(MOUSEEVENTF_LEFTDOWN, 0), Mouse(MOUSEEVENTF_LEFTUP, 0)]);
    }

    private static void Wheel(int delta) => Send([Mouse(MOUSEEVENTF_WHEEL, delta)]);

    /// <summary>One character as a key of its own (the char is the scan code); press and release are sent apart.</summary>
    private static void Unicode(char ch)
    {
        Send([Key(0, ch, KEYEVENTF_UNICODE)]);
        Thread.Sleep(2);
        Send([Key(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP)]);
    }

    /// <summary>Writes a single-line field through its ValuePattern; false when it has none or refuses.</summary>
    private static bool SetValue(AutomationElement e, string text)
    {
        try
        {
            if (!e.TryGetCurrentPattern(ValuePattern.Pattern, out var p) || p is not ValuePattern vp || vp.Current.IsReadOnly) return false;
            vp.SetValue(text);
            Thread.Sleep(60);
            return vp.Current.Value == text;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException or COMException) { return false; }
    }

    /// <summary>A shortcut: modifiers down, the key, modifiers up in reverse.</summary>
    private static void Keys(KeyCombo combo)
    {
        if (Vk(combo.Key) is not (ushort vk, bool extended)) return;
        var mods = new List<ushort>();
        if (combo.Ctrl) mods.Add(VK_CONTROL);
        if (combo.Alt) mods.Add(VK_MENU);
        if (combo.Shift) mods.Add(VK_SHIFT);
        var inputs = new List<INPUT>();
        foreach (var m in mods) inputs.Add(Key(m, 0, 0));
        uint ext = extended ? KEYEVENTF_EXTENDEDKEY : 0;
        inputs.Add(Key(vk, 0, ext));
        inputs.Add(Key(vk, 0, ext | KEYEVENTF_KEYUP));
        for (int i = mods.Count - 1; i >= 0; i--) inputs.Add(Key(mods[i], 0, KEYEVENTF_KEYUP));
        Send([.. inputs]);
    }

    private static (ushort vk, bool extended)? Vk(string key) => key switch
    {
        "enter" => (0x0D, false), "escape" => (0x1B, false), "tab" => (0x09, false), "space" => (0x20, false),
        "backspace" => (0x08, false), "delete" => (0x2E, true), "insert" => (0x2D, true),
        "up" => (0x26, true), "down" => (0x28, true), "left" => (0x25, true), "right" => (0x27, true),
        "home" => (0x24, true), "end" => (0x23, true), "pageup" => (0x21, true), "pagedown" => (0x22, true),
        ['f', .. var n] when int.TryParse(n, out int f) && f is >= 1 and <= 12 => ((ushort)(0x6F + f), false),
        [char c] when char.IsAsciiLetterOrDigit(c) => (char.ToUpperInvariant(c), false),
        _ => null,
    };

    private static INPUT Key(ushort vk, ushort scan, uint flags) =>
        new() { Type = INPUT_KEYBOARD, U = new InputUnion { Ki = new KEYBDINPUT { Vk = vk, Scan = scan, Flags = flags } } };

    private static INPUT Mouse(uint flags, int data) =>
        new() { Type = INPUT_MOUSE, U = new InputUnion { Mi = new MOUSEINPUT { MouseData = unchecked((uint)data), Flags = flags } } };

    private static void Send(INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length) Log.Info($"app: SendInput {sent}/{inputs.Length} (finestra di un amministratore?)");
    }

    private static RECT Frame(IntPtr h)
    {
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0) return r;
        GetWindowRect(h, out r);
        return r;
    }

    /// <summary>Runs on a pool thread aware of each monitor's DPI (physical pixels everywhere), then puts it back.</summary>
    private static Task<T> Run<T>(Func<T> work, CancellationToken ct) => Task.Run(() =>
    {
        IntPtr old = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try { return work(); }
        finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
    }, ct);

    // ---- Win32 ----

    private const int GW_OWNER = 4, GWL_EXSTYLE = -20, GA_ROOT = 2, SW_RESTORE = 9, SW_SHOWNOACTIVATE = 4;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    private const uint PW_RENDERFULLCONTENT = 2, SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    private const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;
    private const uint MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4, MOUSEEVENTF_WHEEL = 0x800;
    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int Dx, Dy; public uint MouseData, Flags, Time; public nuint ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public nuint ExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT Mi; [FieldOffset(0)] public KEYBDINPUT Ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public InputUnion U; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER Header; public uint C0, C1, C2; }

    private delegate bool EnumWindowsProc(IntPtr h, IntPtr param);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, char[] text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, int flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out int value, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFO info, uint usage);
}
