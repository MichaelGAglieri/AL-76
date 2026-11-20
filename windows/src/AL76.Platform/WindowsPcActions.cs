using System.Diagnostics;
using System.Runtime.InteropServices;
using AL76.Core.Diagnostics;
using AL76.Core.Tools;
using AL76.Core.Voice;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace AL76.Platform;

/// <summary>
/// The PC actions of phase 2 on Windows. Apps come from the Start menu's "All apps" list (shell:AppsFolder),
/// which has both classic programs and Store apps (Calcolatrice is one) under the names the user sees, in
/// the language of Windows. Closing asks the window to close, like the X button, so unsaved work prompts.
/// Volume through Core Audio; music through the media session of Windows (Spotify, browsers, players),
/// falling back to the media keys.
/// </summary>
public sealed class WindowsPcActions : IPcActions
{
    private readonly object gate = new();
    private List<(string name, string id)>? apps;
    private DateTime appsAt;

    /// <summary>Name and AppUserModelID of every app in the Start menu, refreshed every 10 minutes.</summary>
    public IReadOnlyList<(string name, string id)> Apps()
    {
        lock (gate)
        {
            if (apps is not null && DateTime.Now - appsAt < TimeSpan.FromMinutes(10)) return apps;
            var list = new List<(string, string)>();
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                dynamic shell = Activator.CreateInstance(type!)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name;
                    string id = item.Path;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id)) list.Add((name, id));
                }
            }
            catch (Exception e) { Log.Info($"app: elenco del menu Start non letto: {e.Message}"); }
            apps = list;
            appsAt = DateTime.Now;
            Log.Info($"app: {list.Count} nel menu Start");
            return list;
        }
    }

    public IReadOnlyList<string> InstalledApps() => Apps().Select(a => a.name).Distinct().OrderBy(n => n).ToList();

    public string? FindApp(string spoken) => Best(spoken, Apps().Select(a => a.name))?.ToString();

    /// <summary>
    /// The closest name: equal, then starting with or containing the spoken words, then a small spelling
    /// difference (Whisper writes "Vord", "Spotifai"). Uninstallers and help pages never count.
    /// </summary>
    public static string? Best(string spoken, IEnumerable<string> names)
    {
        string s = TextTools.NormalizePhrase(spoken);
        if (s.Length == 0) return null;
        var usable = names.Where(n => !n.Contains("uninstall", StringComparison.OrdinalIgnoreCase) && !n.Contains("disinstalla", StringComparison.OrdinalIgnoreCase)
                                      && !n.Contains("readme", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        var norm = usable.Select(n => (n, k: TextTools.NormalizePhrase(n))).ToList();
        string Squash(string x) => x.Replace(" ", "");
        return norm.FirstOrDefault(x => x.k == s).n
            ?? norm.FirstOrDefault(x => Squash(x.k) == Squash(s)).n
            ?? norm.Where(x => (" " + x.k + " ").Contains(" " + s + " ")).OrderBy(x => x.k.Length).FirstOrDefault().n
            ?? norm.Where(x => x.k.StartsWith(s)).OrderBy(x => x.k.Length).FirstOrDefault().n
            ?? (s.Length >= 4 ? norm.Select(x => (x.n, d: TextTools.Distance(Squash(x.k), Squash(s))))
                                   .Where(x => x.d <= Math.Max(1, s.Length / 4)).OrderBy(x => x.d).FirstOrDefault().n : null);
    }

    public Task<PcResult> OpenAppAsync(string name, CancellationToken ct = default)
    {
        var all = Apps();
        if (Best(name, all.Select(a => a.name)) is not string found)
            return Task.FromResult(new PcResult(false, $"No installed app called {name}.", NotFound: true));
        string id = all.First(a => a.name == found).id;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{id}") { UseShellExecute = false });
            Log.Audit("app aperta", found);
            return Task.FromResult(new PcResult(true, $"{found} opened."));
        }
        catch (Exception e) { return Task.FromResult(new PcResult(false, $"{found} did not open: {e.Message}")); }
    }

    private static List<Process> Windows()
    {
        var list = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0 && p.Id != Environment.ProcessId) list.Add(p); }
            catch (InvalidOperationException) { }
        }
        return list;
    }

    private static string Label(Process p)
    {
        try { return p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d ? (d.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? d[..^4] : d) : p.ProcessName; }
        catch (Exception) { return p.ProcessName; }
    }

    public IReadOnlyList<string> RunningApps() =>
        Windows().Select(p => p.ProcessName == "ApplicationFrameHost" ? p.MainWindowTitle : Label(p)).Distinct().OrderBy(n => n).ToList();

    public async Task<PcResult> QuitAppAsync(string name, CancellationToken ct = default)
    {
        var windows = Windows();
        var match = windows.Where(p =>
        {
            var labels = new[] { p.ProcessName, Label(p), p.MainWindowTitle };
            return Best(name, labels) is not null;
        }).ToList();
        if (match.Count == 0) return new PcResult(false, $"{name} is not open.", NotFound: true);
        foreach (var p in match) p.CloseMainWindow();
        await Task.Delay(1500, ct);
        int left = match.Count(p => { try { p.Refresh(); return !p.HasExited; } catch { return false; } });
        Log.Audit("app chiusa", name);
        return left == 0 ? new PcResult(true, $"{name} closed.")
                         : new PcResult(true, $"{name} was asked to close; it may be waiting for the user to save.");
    }

    public Task<PcResult> SetVolumeAsync(int? level, int? change, bool? mute, CancellationToken ct = default)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var volume = device.AudioEndpointVolume;
            if (mute is bool m) volume.Mute = m;
            float now = volume.MasterVolumeLevelScalar * 100;
            if (level is int l) now = l;
            else if (change is int c) now += c;
            if (level is not null || change is not null)
            {
                volume.MasterVolumeLevelScalar = Math.Clamp(now, 0, 100) / 100f;
                if (volume.Mute && now > 0) volume.Mute = false;
            }
            return Task.FromResult(new PcResult(true, $"Volume {(int)Math.Round(volume.MasterVolumeLevelScalar * 100)}%{(volume.Mute ? ", muted" : "")}."));
        }
        catch (Exception e) { return Task.FromResult(new PcResult(false, $"Volume not changed: {e.Message}")); }
    }

    public async Task<PcResult> MediaAsync(MediaKey key, CancellationToken ct = default)
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (manager.GetCurrentSession() is { } session)
            {
                bool ok = key switch
                {
                    MediaKey.Play => await session.TryPlayAsync(),
                    MediaKey.Pause => await session.TryPauseAsync(),
                    MediaKey.Next => await session.TrySkipNextAsync(),
                    MediaKey.Previous => await session.TrySkipPreviousAsync(),
                    _ => await session.TryTogglePlayPauseAsync(),
                };
                if (ok) return new PcResult(true, $"{key} sent to {session.SourceAppUserModelId}.");
            }
        }
        catch (Exception e) { Log.Info($"musica: sessione multimediale non disponibile: {e.Message}"); }
        // No media session (nothing playing yet): the media keys, which start the last player.
        byte vk = key switch { MediaKey.Next => 0xB0, MediaKey.Previous => 0xB1, _ => 0xB3 };
        keybd_event(vk, 0, 0, 0);
        keybd_event(vk, 0, 2, 0);
        return new PcResult(true, $"{key} key pressed.");
    }

    public Task<PcResult> OpenUrlAsync(Uri url, CancellationToken ct = default)
    {
        if (url.Scheme is not ("http" or "https")) return Task.FromResult(new PcResult(false, "Only http and https."));
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        Log.Audit("sito aperto", url.Host);
        return Task.FromResult(new PcResult(true, $"{url.Host} opened in the browser."));
    }

    public Task<PcResult> BatteryAsync(CancellationToken ct = default)
    {
        if (!GetSystemPowerStatus(out var status)) return Task.FromResult(new PcResult(false, "Battery state not readable."));
        // BatteryFlag 128 = no system battery; 255 = unknown.
        if (status.BatteryFlag == 128 || status.BatteryLifePercent == 255) return Task.FromResult(new PcResult(true, "No battery: a desktop PC on mains power."));
        bool charging = (status.BatteryFlag & 8) != 0 || status.ACLineStatus == 1;
        string minutes = status.BatteryLifeTime > 0 && !charging ? $", about {status.BatteryLifeTime / 60} minutes left" : "";
        return Task.FromResult(new PcResult(true, $"Battery {status.BatteryLifePercent}%, {(charging ? "charging" : "on battery")}{minutes}."));
    }

    /// <summary>Desktop, Documents, Downloads... by their name in Italian or English, or a folder of that name inside them.</summary>
    public static string? KnownFolder(string name)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string n = TextTools.NormalizePhrase(name);
        return n switch
        {
            "desktop" or "scrivania" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "documents" or "documenti" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "downloads" or "download" => Path.Combine(home, "Downloads"),
            "pictures" or "immagini" or "foto" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" or "musica" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" or "video" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            _ => null,
        };
    }

    public Task<PcResult> OpenFolderAsync(string name, CancellationToken ct = default)
    {
        string? path = KnownFolder(name);
        if (path is null)
        {
            // A folder of that name one level inside the usual places (not a disk-wide search).
            string n = TextTools.NormalizePhrase(name);
            foreach (var root in new[] { "desktop", "documents", "downloads", "pictures", "music", "videos" }.Select(KnownFolder).OfType<string>())
            {
                try
                {
                    path = Directory.EnumerateDirectories(root).FirstOrDefault(d => TextTools.NormalizePhrase(Path.GetFileName(d)) == n);
                    if (path is not null) break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        if (path is null || !Directory.Exists(path)) return Task.FromResult(new PcResult(false, $"No folder called {name} on the desktop or in the user's folders.", NotFound: true));
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
        return Task.FromResult(new PcResult(true, $"{Path.GetFileName(path)} opened in File Explorer."));
    }

    /// <summary>A file or folder with its usual app (ShellExecute), or selected in File Explorer.</summary>
    public Task<PcResult> OpenPathAsync(string path, bool reveal, CancellationToken ct = default)
    {
        try
        {
            if (reveal) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Log.Audit(reveal ? "file mostrato" : "file aperto", path);
            return Task.FromResult(new PcResult(true, reveal ? $"{Path.GetFileName(path)} shown in File Explorer." : $"{Path.GetFileName(path)} opened."));
        }
        catch (Exception e) { return Task.FromResult(new PcResult(false, $"Could not open {Path.GetFileName(path)}: {e.Message}")); }
    }

    public Task<PcResult> CreateFolderAsync(string name, string? place, CancellationToken ct = default)
    {
        string clean = string.Join("", name.Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
        if (clean.Length == 0 || clean is "." or "..") return Task.FromResult(new PcResult(false, "Invalid folder name."));
        string root = KnownFolder(place ?? "documents") ?? KnownFolder("documents")!;
        string path = Path.Combine(root, clean);
        if (Directory.Exists(path) || File.Exists(path)) return Task.FromResult(new PcResult(false, $"{clean} already exists there: nothing was replaced."));
        Directory.CreateDirectory(path);
        Log.Audit("cartella creata", path);
        return Task.FromResult(new PcResult(true, $"Folder created: {path}"));
    }

    /// <summary>The user's own theme setting (HKCU): apps and the taskbar follow it at once.</summary>
    public Task<PcResult> SetDarkModeAsync(bool on, CancellationToken ct = default)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            key.SetValue("AppsUseLightTheme", on ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", on ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);
            // Tell open windows the setting changed.
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "ImmersiveColorSet", 2, 1000, out _);
            Log.Audit("tema", on ? "scuro" : "chiaro");
            return Task.FromResult(new PcResult(true, on ? "Dark mode on." : "Light mode on."));
        }
        catch (Exception e) { return Task.FromResult(new PcResult(false, "Theme not changed: " + e.Message)); }
    }

    public async Task<PcResult> SleepDisplayAsync(CancellationToken ct = default)
    {
        // A moment first, so the spoken answer is not cut by the screen going dark.
        await Task.Delay(1500, ct);
        SendMessageTimeout(new IntPtr(0xFFFF), 0x0112, new IntPtr(0xF170), 2, 2, 1000, out _);
        return new PcResult(true, "Screen off.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, nint lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
}
