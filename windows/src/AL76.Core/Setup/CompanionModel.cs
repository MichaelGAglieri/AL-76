using System.IO.Compression;

namespace AL76.Core.Setup;

/// <summary>
/// The ROBOTTINO's 3D model: "Mini Robot" by Onur on Sketchfab (CC-BY 4.0, fan art), never shipped with AL-76.
/// The user downloads it (glTF, free Sketchfab account) and AL-76 installs the zip from the Downloads folder into
/// its Companion folder (a tester had no robot and no way to get it, 23/11).
/// </summary>
public static class CompanionModel
{
    public const string Page = "https://sketchfab.com/3d-models/mini-robot-3454c84d6eff4a9dabbfadbfd1f56524";
    public const string Credit = "\"Mini Robot\" (https://sketchfab.com/3d-models/mini-robot-3454c84d6eff4a9dabbfadbfd1f56524) by Onur (https://sketchfab.com/onurpearl), CC-BY 4.0";

    /// <summary>Only what a glTF model is made of.</summary>
    private static readonly HashSet<string> Kinds = [".gltf", ".bin", ".png", ".jpg", ".jpeg", ".webp", ".ktx2", ".txt"];
    private const long MaxBytes = 400L * 1024 * 1024;

    /// <summary>The newest zip in <paramref name="folder"/> with a scene.gltf inside, or null.</summary>
    public static string? FindZip(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.zip").OrderByDescending(f => f.LastWriteTimeUtc).Take(30))
        {
            try
            {
                using var zip = ZipFile.OpenRead(file.FullName);
                if (Scene(zip) is not null) return file.FullName;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static ZipArchiveEntry? Scene(ZipArchive zip) =>
        zip.Entries.Where(e => e.Name.Equals("scene.gltf", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName.Length).FirstOrDefault();

    /// <summary>Unpacks the model (the folder of its scene.gltf) into <paramref name="target"/>; null when done,
    /// otherwise what went wrong. Nothing outside the target, nothing but model files.</summary>
    public static string? Install(string zipPath, string target)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (Scene(zip) is not { } scene) return "nello zip non c'è scene.gltf: su Sketchfab scegli il formato glTF";
            string root = scene.FullName[..^scene.Name.Length];
            var entries = zip.Entries.Where(e => e.FullName.StartsWith(root, StringComparison.Ordinal) && e.Name.Length > 0
                                                 && Kinds.Contains(Path.GetExtension(e.Name).ToLowerInvariant())).ToList();
            if (entries.Sum(e => e.Length) > MaxBytes) return "lo zip è troppo grande per essere il modello del robottino";
            string full = Path.GetFullPath(target);
            string temp = full.TrimEnd(Path.DirectorySeparatorChar) + ".nuovo";
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            Directory.CreateDirectory(temp);
            foreach (var e in entries)
            {
                string path = Path.GetFullPath(Path.Combine(temp, e.FullName[root.Length..]));
                if (!path.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                e.ExtractToFile(path, overwrite: true);
            }
            File.WriteAllText(Path.Combine(temp, "CREDITI.txt"), "This work is based on " + Credit + ".\n");
            if (Directory.Exists(full)) Directory.Delete(full, true);
            Directory.Move(temp, full);
            return File.Exists(Path.Combine(full, "scene.gltf")) ? null : "scene.gltf non copiato";
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return e.Message; }
    }
}
