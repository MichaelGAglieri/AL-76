namespace AL76.Core.Diagnostics;

/// <summary>
/// The two logs, as on the Mac: diagnostica.log (what happens) and sicurezza.log (every action with
/// effects outside AL-76). Plain text, one line per event, in the data folder.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? folder;

    public static void Start(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        folder = dataFolder;
    }

    public static void Info(string message) => Write("diagnostica.log", message);

    /// <summary>An action with effects outside AL-76 (opening an app, sending, deleting...).</summary>
    public static void Audit(string action, string detail) => Write("sicurezza.log", $"{action}: {detail}");

    private static void Write(string file, string message)
    {
        if (folder is null) return;
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(Path.Combine(folder, file), line); }
            catch (IOException) { /* a log must never stop the app */ }
        }
    }
}
