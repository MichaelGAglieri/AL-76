namespace AL76.Linux.Platform;

/// <summary>
/// Where AL-76 keeps its things on Linux, by the XDG base directory rules (Windows: AppPaths).
/// Settings in ~/.config/AL-76, data and models in ~/.local/share/AL-76, logs in ~/.local/state/AL-76.
/// </summary>
public static class XdgPaths
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>An XDG variable, or its default under the home folder when it is unset, empty or relative (as the spec says).</summary>
    public static string Base(string variable, string fallback, Func<string, string?>? env = null)
    {
        string? value = (env ?? Environment.GetEnvironmentVariable)(variable);
        return !string.IsNullOrEmpty(value) && Path.IsPathRooted(value) ? value : Path.Combine(Home, fallback);
    }

    public static string Config { get; } = Path.Combine(Base("XDG_CONFIG_HOME", ".config"), "AL-76");
    /// <summary>Memory, reminders, notes index, secrets' metadata: small and personal.</summary>
    public static string Data { get; } = Path.Combine(Base("XDG_DATA_HOME", Path.Combine(".local", "share")), "AL-76");
    public static string State { get; } = Path.Combine(Base("XDG_STATE_HOME", Path.Combine(".local", "state")), "AL-76");
    /// <summary>Whisper and voice models, downloaded at first start.</summary>
    public static string Models => Path.Combine(Data, "models");
    /// <summary>The web views' cache (nothing personal).</summary>
    public static string Browser => Path.Combine(Base("XDG_CACHE_HOME", ".cache"), "AL-76", "web");
    public static string ConfigFile => Path.Combine(Config, "config.json");
    /// <summary>Where the windows were and whether the sphere was reduced.</summary>
    public static string WindowsFile => Path.Combine(Config, "finestre.json");

    /// <summary>The ROBOTTINO's 3D model, put there by the user (scene.gltf with scene.bin and textures). Never part of AL-76.</summary>
    public static string Companion => Path.Combine(Data, "companion");
    public static bool CompanionModel => File.Exists(Path.Combine(Companion, "scene.gltf"));

    /// <summary>The single-instance socket: in the user's runtime folder (only they can open it), else in the state folder.</summary>
    public static string Socket
    {
        get
        {
            string? run = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return !string.IsNullOrEmpty(run) && Directory.Exists(run) ? Path.Combine(run, "al76.sock") : Path.Combine(State, "al76.sock");
        }
    }

    /// <summary>
    /// The user's named folders from ~/.config/user-dirs.dirs (DESKTOP, DOCUMENTS, DOWNLOAD...): on an Italian system they
    /// are "Scrivania" and "Documenti". Missing entries fall back to the English names under home.
    /// </summary>
    public static string UserDir(string key, string? userDirsFile = null)
    {
        string file = userDirsFile ?? Path.Combine(Base("XDG_CONFIG_HOME", ".config"), "user-dirs.dirs");
        try
        {
            foreach (var raw in File.ReadLines(file))
            {
                string line = raw.Trim();
                string prefix = $"XDG_{key}_DIR=";
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string value = line[prefix.Length..].Trim().Trim('"');
                value = value.Replace("$HOME", Home);
                if (Path.IsPathRooted(value)) return value;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Path.Combine(Home, key switch
        {
            "DESKTOP" => "Desktop", "DOCUMENTS" => "Documents", "DOWNLOAD" => "Downloads", "MUSIC" => "Music",
            "PICTURES" => "Pictures", "VIDEOS" => "Videos", "TEMPLATES" => "Templates", "PUBLICSHARE" => "Public",
            _ => key.ToLowerInvariant(),
        });
    }
}
