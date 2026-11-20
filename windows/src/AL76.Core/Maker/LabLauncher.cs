using System.Diagnostics;

namespace AL76.Core.Maker;

/// <summary>
/// Opens the whole ROS 2 laboratory (Webots, nodes, outcomes, console) like «pixi run finestre» (Mac: RosBridge.startLab).
/// The project folder comes from the settings (LabProject); pixi from PATH or its usual place in the user folder.
/// </summary>
public static class LabLauncher
{
    private static string L(bool it, string i, string e) => it ? i : e;

    public static string? Pixi()
    {
        string exe = OperatingSystem.IsWindows() ? "pixi.exe" : "pixi";
        var places = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pixi", "bin"));
        return places.Select(p => Path.Combine(p, exe)).FirstOrDefault(File.Exists);
    }

    public static string Start(string project, bool it)
    {
        if (project.Length == 0) return L(it, "La cartella del laboratorio non è impostata (Impostazioni › Moduli › Laboratorio ROS 2).", "The lab folder is not set (Settings › Modules › ROS 2 lab).");
        string manifest = Path.Combine(project, "pixi.toml");
        if (!File.Exists(manifest)) return L(it, $"In {project} non c'è pixi.toml.", $"There is no pixi.toml in {project}.");
        if (Pixi() is not { } pixi) return L(it, "pixi non trovato: installalo da pixi.sh.", "pixi not found: install it from pixi.sh.");
        try
        {
            var info = new ProcessStartInfo(pixi) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = project };
            foreach (var a in new[] { "run", "--manifest-path", manifest, "finestre" }) info.ArgumentList.Add(a);
            Process.Start(info)?.Dispose();
            return L(it, "Avvio del laboratorio in corso: compilazione, poi Webots e le finestre dei nodi. Premi Play in Webots per iniziare.",
                     "Starting the lab: build, then Webots and the node windows. Press Play in Webots to begin.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return L(it, $"Avvio non riuscito: {e.Message}", $"Start failed: {e.Message}");
        }
    }
}
