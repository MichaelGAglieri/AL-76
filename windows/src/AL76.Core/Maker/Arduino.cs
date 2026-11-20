using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace AL76.Core.Maker;

/// <summary>Runs a program and gives back what it printed; at the time limit the program is closed and what came so far is kept.</summary>
public interface IShell
{
    Task<(int code, string output, string error)> RunAsync(string exe, IEnumerable<string> args, TimeSpan limit, CancellationToken ct);
}

public sealed class ProcessShell : IShell
{
    public async Task<(int code, string output, string error)> RunAsync(string exe, IEnumerable<string> args, TimeSpan limit, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info) ?? throw new InvalidOperationException(exe);
        p.StandardInput.Close();
        var output = new StringBuilder();
        var error = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (error) error.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(limit);
        int code;
        try { await p.WaitForExitAsync(timer.Token); code = p.ExitCode; }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            code = -1;   // the time limit: the serial monitor ends this way
        }
        lock (output) lock (error) return (code, output.ToString(), error.ToString());
    }
}

public sealed record Board(string Port, string Name, string? Fqbn);

/// <summary>
/// Arduino through arduino-cli (the one inside Arduino IDE 2 works): connected boards, compiling, uploading,
/// reading the serial port (Mac: ArduinoActions). Sketches are looked for in the maker folders of the settings.
/// </summary>
public sealed class Arduino(Func<string> cliSetting, Func<IReadOnlyList<string>> folders, IShell shell)
{
    public string? Cli()
    {
        string set = cliSetting().Trim().Trim('"');
        if (set.Length > 0) return File.Exists(set) ? set : null;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] places =
        [
            Path.Combine(local, "Programs", "Arduino IDE", "resources", "app", "lib", "backend", "resources", "arduino-cli.exe"),
            Path.Combine(programs, "Arduino IDE", "resources", "app", "lib", "backend", "resources", "arduino-cli.exe"),
            Path.Combine(programs, "Arduino CLI", "arduino-cli.exe"),
        ];
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(d => new[] { Path.Combine(d, "arduino-cli.exe"), Path.Combine(d, "arduino-cli") });
        return places.Concat(onPath).FirstOrDefault(File.Exists);
    }

    private static string L(bool it, string i, string e) => it ? i : e;
    private string Missing(bool it) => L(it, "arduino-cli non trovato: installa Arduino IDE 2 oppure indica arduino-cli.exe nelle Impostazioni › Moduli.",
        "arduino-cli not found: install Arduino IDE 2 or point to arduino-cli.exe in Settings › Modules.");

    public async Task<List<Board>> BoardsAsync(CancellationToken ct)
    {
        if (Cli() is not { } cli) return [];
        var (_, output, _) = await shell.RunAsync(cli, ["board", "list", "--format", "json"], TimeSpan.FromSeconds(20), ct);
        return ParseBoards(output);
    }

    public static List<Board> ParseBoards(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch (System.Text.Json.JsonException) { return []; }
        var list = (root as JsonArray) ?? root?["detected_ports"]?.AsArray() ?? [];
        var boards = new List<Board>();
        foreach (var entry in list)
        {
            var port = entry?["port"];
            string address = port?["address"]?.ToString() ?? "";
            if (address.Length == 0 || port?["protocol"]?.ToString() != "serial") continue;
            var match = entry?["matching_boards"]?.AsArray().FirstOrDefault();
            boards.Add(new Board(address, match?["name"]?.ToString() ?? "scheda sconosciuta", match?["fqbn"]?.ToString()));
        }
        return boards;
    }

    public async Task<string> ListAsync(bool it, CancellationToken ct)
    {
        if (Cli() is null) return Missing(it);
        var boards = await BoardsAsync(ct);
        return boards.Count == 0 ? L(it, "Nessuna scheda Arduino collegata via USB.", "No Arduino board connected over USB.")
            : L(it, "Schede collegate:\n", "Connected boards:\n") + string.Join("\n", boards.Select(b => $"- {b.Name} su {b.Port}{(b.Fqbn is null ? "" : $" ({b.Fqbn})")}"));
    }

    /// <summary>A sketch is a folder with a .ino of the same name; the closest name wins.</summary>
    public string? FindSketch(string query)
    {
        static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        string q = Norm(query);
        if (q.Length == 0) return null;
        var sketches = new List<string>();
        foreach (var root in folders().Where(Directory.Exists))
        {
            try
            {
                sketches.AddRange(Directory.EnumerateFiles(root, "*.ino", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 })
                    .Where(f => Path.GetFileNameWithoutExtension(f) == Path.GetFileName(Path.GetDirectoryName(f)))
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}build{Path.DirectorySeparatorChar}")));
            }
            catch (IOException) { }
        }
        var found = sketches.FirstOrDefault(f => Norm(Path.GetFileNameWithoutExtension(f)) == q) ?? sketches.FirstOrDefault(f => Norm(f).Contains(q));
        return found is null ? null : Path.GetDirectoryName(found);
    }

    private static string Lines(string text, Func<string, bool> keep, int count) =>
        string.Join("\n", text.Split('\n').Select(l => l.TrimEnd()).Where(keep).Take(count));

    public async Task<string> CompileAsync(string sketchName, string? fqbn, bool it, CancellationToken ct)
    {
        if (Cli() is not { } cli) return Missing(it);
        if (FindSketch(sketchName) is not { } sketch) return L(it, "Sketch non trovato nelle cartelle Maker: indica il nome del progetto o del file .ino.", "Sketch not found in the Maker folders.");
        var board = (await BoardsAsync(ct)).FirstOrDefault();
        string? chosen = fqbn ?? board?.Fqbn;
        if (chosen is null) return L(it, "Non so per che scheda compilare: collegala via USB o dimmi il modello (per esempio arduino:avr:uno).", "Which board? Connect it over USB or tell me the FQBN (e.g. arduino:avr:uno).");
        var (code, output, error) = await shell.RunAsync(cli, ["compile", "--fqbn", chosen, sketch], TimeSpan.FromMinutes(4), ct);
        string name = Path.GetFileName(sketch);
        if (code == 0)
            return L(it, $"Compilazione riuscita di {name} per {chosen}. ", $"{name} compiled for {chosen}. ") + Lines(output, l => l.Contains("byte"), 2).Replace("\n", " ");
        string errors = Lines(output + "\n" + error, l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("errore"), 5);
        return L(it, $"Compilazione fallita di {name}:\n", $"{name} did not compile:\n") + (errors.Length > 0 ? errors : error[^Math.Min(800, error.Length)..]);
    }

    public async Task<string> UploadAsync(string sketchName, string? port, string? fqbn, bool it, CancellationToken ct)
    {
        if (Cli() is not { } cli) return Missing(it);
        if (FindSketch(sketchName) is not { } sketch) return L(it, "Sketch non trovato.", "Sketch not found.");
        var boards = await BoardsAsync(ct);
        var board = boards.FirstOrDefault(b => string.IsNullOrEmpty(port) || b.Port.Equals(port, StringComparison.OrdinalIgnoreCase));
        if (board is null) return L(it, "Nessuna scheda collegata su cui caricare.", "No connected board to upload to.");
        string? chosen = fqbn ?? board.Fqbn;
        if (chosen is null) return L(it, $"Su {board.Port} c'è una scheda che non riconosco: dimmi il modello (FQBN).", $"Unknown board on {board.Port}: tell me its FQBN.");
        var compile = await shell.RunAsync(cli, ["compile", "--fqbn", chosen, sketch], TimeSpan.FromMinutes(4), ct);
        if (compile.code != 0) return L(it, "Compilazione fallita, niente caricamento:\n", "Compilation failed, nothing uploaded:\n") + compile.error[^Math.Min(600, compile.error.Length)..];
        var (code, _, error) = await shell.RunAsync(cli, ["upload", "--fqbn", chosen, "--port", board.Port, "--verify", sketch], TimeSpan.FromMinutes(4), ct);
        if (code == 0) return L(it, $"Caricato {Path.GetFileName(sketch)} su {board.Name} ({board.Port}) e verificato.", $"Uploaded {Path.GetFileName(sketch)} to {board.Name} ({board.Port}) and verified.");
        // A port opened by another program (a serial monitor, a robot console) must be closed there first.
        if (error.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) || error.Contains("Accesso negato", StringComparison.OrdinalIgnoreCase) || error.Contains("busy", StringComparison.OrdinalIgnoreCase))
            return L(it, $"La porta {board.Port} è occupata da un altro programma: chiudi lì il collegamento (monitor seriale, console del robot) e riprova.",
                $"Port {board.Port} is in use by another program: close the connection there and try again.");
        return L(it, "Caricamento fallito: ", "Upload failed: ") + error[^Math.Min(600, error.Length)..];
    }

    /// <summary>Reads the serial port for a few seconds (opening it usually resets the board).</summary>
    public async Task<string> ReadSerialAsync(string? port, int baud, double seconds, bool it, CancellationToken ct)
    {
        if (Cli() is not { } cli) return Missing(it);
        port ??= (await BoardsAsync(ct)).FirstOrDefault()?.Port;
        if (port is null) return L(it, "Nessuna scheda collegata.", "No board connected.");
        seconds = Math.Clamp(seconds, 1, 30);
        var (_, output, error) = await shell.RunAsync(cli, ["monitor", "-p", port, "--config", $"baudrate={baud}", "--quiet"], TimeSpan.FromSeconds(seconds), ct);
        string text = (output + error).Trim();
        return text.Length == 0 ? L(it, $"Nessun dato dalla seriale {port} in {seconds:0} s a {baud} baud.", $"No data from {port} in {seconds:0} s at {baud} baud.")
            : $"{port} ({baud} baud):\n" + text[^Math.Min(2500, text.Length)..];
    }
}
