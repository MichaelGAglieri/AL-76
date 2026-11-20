namespace AL76.Platform;

/// <summary>Where AL-76 keeps its things on Windows.</summary>
public static class AppPaths
{
    /// <summary>%APPDATA%\AL-76: config.json, logs, memory. Small and personal.</summary>
    public static string Data { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AL-76");

    /// <summary>%LOCALAPPDATA%\AL-76\models: Whisper and voice models, downloaded at first start.</summary>
    public static string Models { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AL-76", "models");

    /// <summary>%LOCALAPPDATA%\AL-76\WebView2: the HUD page's browser data (cache only, nothing personal).</summary>
    public static string Browser { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AL-76", "WebView2");

    public static string ConfigFile => Path.Combine(Data, "config.json");

    /// <summary>%APPDATA%\AL-76\Companion: the ROBOTTINO's 3D model, put there by the user (scene.gltf with scene.bin
    /// and textures). It is never part of AL-76: without it the nucleus shows the satellite.</summary>
    public static string Companion => Path.Combine(Data, "Companion");

    public static bool CompanionModel => File.Exists(Path.Combine(Companion, "scene.gltf"));

    /// <summary>Where the windows were and whether the sphere was reduced (Mac: the windows' saved frames).</summary>
    public static string WindowsFile => Path.Combine(Data, "finestre.json");
}
