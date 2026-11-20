namespace AL76.Core.Tools;

public enum MediaKey { PlayPause, Play, Pause, Next, Previous }

/// <summary>How a PC action went. <see cref="Detail"/> is for the model (English); the instant commands say their own sentence.</summary>
public sealed record PcResult(bool Ok, string Detail = "", bool NotFound = false)
{
    public static readonly PcResult Done = new(true);
    public string ForModel => Ok ? (Detail.Length > 0 ? Detail : "Done.") : (Detail.Length > 0 ? Detail : "Failed.");
}

/// <summary>
/// What AL-76 does on the PC, behind an interface: Windows implements it in AL76.Platform, the tests and the
/// brain bench on the Mac use a fake that only records the calls. Phase 2 has the basics (apps, volume,
/// music, web pages); phase 4 adds control inside apps (UI Automation).
/// </summary>
public interface IPcActions
{
    /// <summary>The installed app that best matches a spoken name ("vord" → "Word"), or null.</summary>
    string? FindApp(string name);
    /// <summary>The names of the installed apps, as the Start menu shows them.</summary>
    IReadOnlyList<string> InstalledApps();
    /// <summary>Opens an app by the name the user said ("Word", "calcolatrice").</summary>
    Task<PcResult> OpenAppAsync(string name, CancellationToken ct = default);
    Task<PcResult> QuitAppAsync(string name, CancellationToken ct = default);
    IReadOnlyList<string> RunningApps();
    /// <summary>Volume 0–100 (<paramref name="level"/>), or a step (<paramref name="change"/>), or mute on/off.</summary>
    Task<PcResult> SetVolumeAsync(int? level, int? change, bool? mute, CancellationToken ct = default);
    Task<PcResult> MediaAsync(MediaKey key, CancellationToken ct = default);
    Task<PcResult> OpenUrlAsync(Uri url, CancellationToken ct = default);
    /// <summary>Battery level and charging, or "no battery" on a desktop PC.</summary>
    Task<PcResult> BatteryAsync(CancellationToken ct = default);
    /// <summary>A known folder ("desktop", "documents", "downloads"...) or a folder found by name, in Explorer.</summary>
    Task<PcResult> OpenFolderAsync(string name, CancellationToken ct = default);
    /// <summary>A new folder in a known place (default: Documents). Never replaces an existing one.</summary>
    Task<PcResult> CreateFolderAsync(string name, string? place, CancellationToken ct = default);
    Task<PcResult> SetDarkModeAsync(bool on, CancellationToken ct = default);
    Task<PcResult> SleepDisplayAsync(CancellationToken ct = default);
    /// <summary>Opens a file or folder with its usual app, or shows it selected in File Explorer.</summary>
    Task<PcResult> OpenPathAsync(string path, bool reveal, CancellationToken ct = default);
}

/// <summary>Records the calls and answers "done": for the tests and for the brain bench.</summary>
public sealed class RecordingPcActions : IPcActions
{
    public List<string> Calls { get; } = [];
    public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase)
        { "Word", "Excel", "PowerPoint", "Outlook", "Spotify", "Calcolatrice", "Blocco note", "Microsoft Edge", "Google Chrome", "Esplora file", "Teams", "WhatsApp" };
    public List<string> Running { get; } = ["Microsoft Edge", "Spotify"];

    private PcResult Record(string call) { lock (Calls) Calls.Add(call); return PcResult.Done; }

    public string? FindApp(string name) =>
        Installed.FirstOrDefault(a => a.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? Installed.FirstOrDefault(a => a.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(a, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> InstalledApps() => Installed.OrderBy(a => a).ToList();

    public Task<PcResult> OpenAppAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(FindApp(name) is string app ? Record($"open_app {app}") : new PcResult(false, $"No installed app called {name}.", NotFound: true));
    public Task<PcResult> QuitAppAsync(string name, CancellationToken ct = default) => Task.FromResult(Record($"quit_app {name}"));
    public IReadOnlyList<string> RunningApps() => Running;
    public Task<PcResult> SetVolumeAsync(int? level, int? change, bool? mute, CancellationToken ct = default) =>
        Task.FromResult(Record($"volume level={level} change={change} mute={mute}"));
    public Task<PcResult> MediaAsync(MediaKey key, CancellationToken ct = default) => Task.FromResult(Record($"media {key}"));
    public Task<PcResult> OpenUrlAsync(Uri url, CancellationToken ct = default) => Task.FromResult(Record($"open_url {url.Host}"));
    public Task<PcResult> BatteryAsync(CancellationToken ct = default) { Record("battery"); return Task.FromResult(new PcResult(true, "Battery 76%, charging.")); }
    public Task<PcResult> OpenFolderAsync(string name, CancellationToken ct = default) => Task.FromResult(Record($"open_folder {name}"));
    public Task<PcResult> CreateFolderAsync(string name, string? place, CancellationToken ct = default) => Task.FromResult(Record($"create_folder {name} in {place ?? "documents"}"));
    public Task<PcResult> SetDarkModeAsync(bool on, CancellationToken ct = default) => Task.FromResult(Record($"dark_mode {on}"));
    public Task<PcResult> SleepDisplayAsync(CancellationToken ct = default) => Task.FromResult(Record("sleep_display"));
    public Task<PcResult> OpenPathAsync(string path, bool reveal, CancellationToken ct = default) => Task.FromResult(Record($"{(reveal ? "reveal" : "open")} {Path.GetFileName(path)}"));
}
