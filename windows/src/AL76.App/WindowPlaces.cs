using System;
using System.IO;
using System.Text.Json;
using AL76.Platform;

namespace AL76.App;

/// <summary>Where the windows were and whether the sphere was reduced to the nucleus (Mac: the saved window frames
/// and "compactMode", "voiceBubble"). Kept apart from the settings: it changes by itself, every time the user moves something.</summary>
public sealed class WindowPlaces
{
    public bool Compact { get; set; }
    public double? NucleusLeft { get; set; }
    public double? NucleusTop { get; set; }
    /// <summary>The ROBOTTINO also says spoken answers in its speech bubble (Mac: voiceBubble, set from its menu).</summary>
    public bool VoiceBubble { get; set; }
    /// <summary>The GPU on which the sphere could not keep up: with it the app shows only the nucleus. Another GPU
    /// (a new PC, a driver that now works) gets the sphere again.</summary>
    public string? HeavySphereGpu { get; set; }

    public static WindowPlaces Load()
    {
        try
        {
            if (File.Exists(AppPaths.WindowsFile))
                return JsonSerializer.Deserialize<WindowPlaces>(File.ReadAllText(AppPaths.WindowsFile)) ?? new WindowPlaces();
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return new WindowPlaces();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(AppPaths.WindowsFile, JsonSerializer.Serialize(this));
        }
        catch (IOException) { }
    }
}
