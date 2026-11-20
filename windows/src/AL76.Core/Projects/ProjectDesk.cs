using System.Globalization;
using System.Text.Json.Nodes;
using AL76.Core.Maker;
using AL76.Core.Safety;

namespace AL76.Core.Projects;

/// <summary>
/// The user's projects (Mac: ProjectInsights): the folders in the project places (default: Projects in the user
/// folder, Visual Studio's source\repos, Documents\GitHub), their README, git state and recent work, and public
/// repositories downloaded with git. Collections ("Lab Projects") count their subfolders as projects.
/// </summary>
public sealed class ProjectDesk(Func<IReadOnlyList<string>> places, IShell shell, Func<string, bool> allowed)
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", "build", "install", "log", ".venv", "venv", "__pycache__", ".build", ".vs", ".idea", "dist", "out", "target" };

    public static IReadOnlyList<string> DefaultPlaces()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return [Path.Combine(home, "Projects"), Path.Combine(home, "source", "repos"), Path.Combine(docs, "GitHub"), Path.Combine(docs, "Projects")];
    }

    public IReadOnlyList<string> Roots() => places().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Project folders: those in the places and, for collections whose name ends in "projects", those inside.</summary>
    public List<string> Folders()
    {
        static IEnumerable<string> Dirs(string d)
        {
            try { return Directory.EnumerateDirectories(d).Where(x => !Path.GetFileName(x).StartsWith('.')); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }
        return Roots().SelectMany(root => Dirs(root).SelectMany(d => Path.GetFileName(d).EndsWith("projects", StringComparison.OrdinalIgnoreCase) ? Dirs(d) : [d]))
            .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string? Find(string name)
    {
        static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        string q = Norm(name);
        if (q.Length == 0) return null;
        var folders = Folders();
        return folders.FirstOrDefault(f => Norm(Path.GetFileName(f)) == q)
            ?? folders.FirstOrDefault(f => Norm(Path.GetFileName(f)).StartsWith(q))
            ?? folders.FirstOrDefault(f => Norm(Path.GetFileName(f)).Contains(q) || q.Contains(Norm(Path.GetFileName(f))));
    }

    public string List(bool it)
    {
        var folders = Folders();
        if (folders.Count == 0)
            return it ? "Non trovo cartelle di progetti (Progetti nella cartella utente, source\\repos, Documenti\\GitHub o quelle scelte nelle Impostazioni)."
                      : "No project folders found (Projects in the user folder, source\\repos, Documents\\GitHub or those chosen in Settings).";
        return (it ? "Progetti: " : "Projects: ") + string.Join(", ", folders.Select(Path.GetFileName));
    }

    public async Task<string> InfoAsync(string name, bool it, CancellationToken ct)
    {
        if (Find(name) is not string dir)
            return (it ? "Progetto non trovato. " : "Project not found. ")
                   + "If the name may come from the news given today, call news_detail with it instead. " + List(it);
        var sb = new System.Text.StringBuilder($"{(it ? "Progetto" : "Project")} {Path.GetFileName(dir)} ({dir})\n");
        foreach (var readme in new[] { "README.md", "readme.md", "README.txt", "README", "README.rst" })
        {
            string file = Path.Combine(dir, readme);
            if (!File.Exists(file)) continue;
            try { sb.Append("\nREADME:\n").Append(Cut(await File.ReadAllTextAsync(file, ct), 1500)).Append('\n'); } catch (IOException) { }
            break;
        }
        if (Directory.Exists(Path.Combine(dir, ".git")))
        {
            var log = await Git(["-C", dir, "log", "-6", "--format=%ar: %s"], ct);
            var status = await Git(["-C", dir, "status", "--short"], ct);
            var branch = await Git(["-C", dir, "branch", "--show-current"], ct);
            if (log is null) sb.Append(it ? "\nGit non è installato: non leggo i commit.\n" : "\nGit is not installed: commits not read.\n");
            else
            {
                int changed = (status ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                sb.Append(it ? $"\nGit: ramo {branch?.Trim()}, {changed} file modificati non ancora in un commit.\nUltimi commit:\n{log.Trim()}\n"
                             : $"\nGit: branch {branch?.Trim()}, {changed} changed files not committed.\nLast commits:\n{log.Trim()}\n");
            }
        }
        else sb.Append(it ? "\nNon è un repository git.\n" : "\nNot a git repository.\n");
        var recent = RecentFiles(dir, DateTime.Now.AddDays(-14), 10);
        sb.Append(recent.Count == 0 ? (it ? "\nNessun file modificato nelle ultime due settimane." : "\nNo file changed in the last two weeks.")
            : (it ? "\nFile modificati di recente:\n" : "\nRecently changed files:\n") + string.Join("\n", recent.Select(r => $"- {Path.GetRelativePath(dir, r.path)} ({Ago(r.at, it)})")));
        return Security.External(it ? "progetto" : "project", sb.ToString());
    }

    /// <summary>Which projects were worked on in the last days (Mac: Spotlight; here a walk of the folders, at most
    /// 20 000 files, skipping builds, dependencies and git's own files).</summary>
    public string RecentActivity(int days, bool it, DateTime now)
    {
        var since = now.AddDays(-days);
        var rows = new List<(string name, int count, DateTime latest)>();
        foreach (var dir in Folders())
        {
            var files = RecentFiles(dir, since, int.MaxValue);
            if (files.Count > 0) rows.Add((Path.GetFileName(dir), files.Count, files.Max(f => f.at)));
        }
        if (rows.Count == 0) return it ? $"Nessuna modifica ai progetti negli ultimi {days} giorni." : $"No changes to projects in the last {days} days.";
        return (it ? $"Progetti modificati negli ultimi {days} giorni:\n" : $"Projects changed in the last {days} days:\n")
               + string.Join("\n", rows.OrderByDescending(r => r.latest).Select(r => it ? $"- {r.name}: {r.count} file, ultima modifica {Ago(r.latest, true)}"
                                                                                          : $"- {r.name}: {r.count} files, last change {Ago(r.latest, false)}"));
    }

    public static List<(string path, DateTime at)> RecentFiles(string dir, DateTime since, int limit)
    {
        var found = new List<(string, DateTime)>();
        var stack = new Stack<(string dir, int depth)>();
        stack.Push((dir, 0));
        int seen = 0;
        while (stack.Count > 0 && seen < 20_000)
        {
            var (d, depth) = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(d))
                {
                    if (++seen > 20_000) break;
                    var at = File.GetLastWriteTime(f);
                    if (at >= since) found.Add((f, at));
                }
                if (depth < 8)
                    foreach (var sub in Directory.EnumerateDirectories(d))
                        if (!Skipped.Contains(Path.GetFileName(sub)) && !Path.GetFileName(sub).StartsWith('.')) stack.Push((sub, depth + 1));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return found.OrderByDescending(f => f.Item2).Take(limit).ToList();
    }

    /// <summary>A public repository of GitHub, GitLab, Codeberg or Hugging Face into a new folder; nothing installed or run
    /// (hooks off, no submodules, no LFS). A wrong or private address fails in seconds instead of asking a password.</summary>
    public async Task<string> CloneAsync(string link, string folder, string name, bool it, CancellationToken ct)
    {
        link = link.Trim();
        if (link.Length == 0) return it ? "Manca l'indirizzo del repository: cercalo con web_search." : "No repository address: find it with web_search.";
        if (!link.Contains("://")) link = "https://" + link;
        string[] hosts = ["github.com", "gitlab.com", "codeberg.org", "huggingface.co"];
        if (!Uri.TryCreate(link, UriKind.Absolute, out var url) || url.Scheme != "https" || !hosts.Contains(url.Host.ToLowerInvariant().Replace("www.", ""))
            || url.Segments.Length < 3)
            return it ? "Scarico solo repository pubblici https di GitHub, GitLab, Codeberg o Hugging Face, con indirizzo completo (github.com/autore/nome)."
                      : "Only public https repositories of GitHub, GitLab, Codeberg or Hugging Face, with the full address (github.com/owner/name).";
        string owner = url.Segments[1].Trim('/'), repoName = url.Segments[2].Trim('/').Replace(".git", "");
        string repo = $"https://{url.Host.ToLowerInvariant().Replace("www.", "")}/{owner}/{repoName}";
        string parent = folder.Length > 0 ? folder : Roots().FirstOrDefault() ?? DefaultPlaces()[0];
        string target = Path.GetFullPath(Path.Combine(parent, (name.Length > 0 ? name : repoName).Replace('/', '-').Replace('\\', '-')));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!target.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !allowed(target))
            return it ? "Scarico solo dentro la tua cartella utente, non nelle cartelle di sistema." : "Only inside your user folder, not in system folders.";
        if (Directory.Exists(target) || File.Exists(target))
            return it ? $"Esiste già {target}: scegli un altro nome o un'altra cartella." : $"{target} already exists: choose another name or folder.";
        if (await Git(["--version"], ct) is null)
            return it ? "Git non è installato: si scarica da git-scm.com (Git for Windows)." : "Git is not installed: get it from git-scm.com (Git for Windows).";
        Directory.CreateDirectory(parent);
        var check = await shell.RunAsync("git", ["-c", "credential.helper=", "ls-remote", "--heads", repo], TimeSpan.FromSeconds(20), ct);
        if (check.code != 0)
            return it ? $"Il repository {repo} non esiste o non è pubblico. Non inventare l'indirizzo: cercalo con web_search."
                      : $"The repository {repo} does not exist or is not public. Do not guess the address: find it with web_search.";
        var clone = await shell.RunAsync("git", ["-c", "core.hooksPath=NUL", "-c", "credential.helper=", "-c", "filter.lfs.smudge=", "-c", "filter.lfs.process=",
                                                 "clone", "--depth", "1", "--no-recurse-submodules", "--quiet", repo, target], TimeSpan.FromMinutes(5), ct);
        if (clone.code != 0)
        {
            try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return (it ? "Scaricamento non riuscito: " : "Download failed: ") + (clone.error.Trim().Split('\n').LastOrDefault() ?? "git");
        }
        string? readme = new[] { "README.md", "readme.md", "README.rst", "README" }.FirstOrDefault(r => File.Exists(Path.Combine(target, r)));
        return (it ? $"Scaricato {repo} in {target}." : $"Downloaded {repo} into {target}.")
               + (readme is null ? "" : $" There is a {readme}: read it with read_code if the user asks what it contains or how to use it.") + " Nothing was installed or run.";
    }

    /// <summary>Git's output, or null when git is missing or fails to start.</summary>
    private async Task<string?> Git(string[] args, CancellationToken ct)
    {
        try
        {
            var r = await shell.RunAsync("git", args, TimeSpan.FromSeconds(10), ct);
            return r.code == 0 ? r.output : r.code == -1 ? null : r.output;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    public static string Ago(DateTime at, bool it)
    {
        var d = DateTime.Now - at;
        if (d.TotalMinutes < 60) return it ? $"{Math.Max(1, (int)d.TotalMinutes)} minuti fa" : $"{Math.Max(1, (int)d.TotalMinutes)} minutes ago";
        if (d.TotalHours < 24) return it ? $"{(int)d.TotalHours} ore fa" : $"{(int)d.TotalHours} hours ago";
        if (d.TotalDays < 2) return it ? "ieri" : "yesterday";
        return it ? $"{(int)d.TotalDays} giorni fa" : $"{(int)d.TotalDays} days ago";
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>
/// Help with the code open in VS Code (Mac: CodeHelper, 11 November: "guarda il codice che sto scrivendo",
/// "commentalo senza cambiare l'indentazione"). It reads the file itself and changes it only when asked: a copy goes
/// first to backup_codice, and in "comments" mode every line of code must stay exactly as it was.
/// </summary>
public sealed class CodeHelper(string backupFolder, Func<string?> vsCodeTitle, Func<string, bool> allowed, IShell shell)
{
    public static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".py", ".c", ".cpp", ".cc", ".h", ".hpp", ".ino", ".cs", ".js", ".ts", ".java", ".kt", ".rs", ".go", ".m", ".swift", ".sh", ".ps1",
          ".rb", ".lua", ".yaml", ".yml", ".xml", ".launch", ".json", ".toml", ".cmake", ".txt", ".md", ".html", ".css" };
    private static readonly HashSet<string> HashComments = new(StringComparer.OrdinalIgnoreCase) { ".py", ".sh", ".ps1", ".rb", ".yaml", ".yml", ".toml", ".cmake", ".txt" };
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "build", ".venv", "venv", "__pycache__", "install", "log", "bin", "obj", ".git" };

    /// <summary>The file in the front VS Code window: the title gives the name ("● " when unsaved), VS Code's
    /// storage.json the folders. Without a title, the most recently changed code file of those folders.</summary>
    public (string path, bool unsaved)? ActiveFile()
    {
        string raw = vsCodeTitle() ?? "";
        bool unsaved = raw.StartsWith('●');
        string name = raw.Replace("●", "").Split(" - ")[0].Split(" — ")[0].Trim();
        var candidates = new List<string>();
        foreach (var folder in VsCodeFolders())
        {
            var stack = new Stack<(string, int)>();
            stack.Push((folder, 0));
            while (stack.Count > 0 && candidates.Count < 400)
            {
                var (d, depth) = stack.Pop();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(d))
                        if (CodeExtensions.Contains(Path.GetExtension(f)) && (name.Length == 0 || Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)))
                            candidates.Add(f);
                    if (depth < 5)
                        foreach (var s in Directory.EnumerateDirectories(d))
                            if (!Skipped.Contains(Path.GetFileName(s)) && !Path.GetFileName(s).StartsWith('.')) stack.Push((s, depth + 1));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        var newest = candidates.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        return newest is null ? null : (newest, unsaved && (name.Length == 0 || Path.GetFileName(newest).Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Folders open in VS Code, from %APPDATA%\Code\User\globalStorage\storage.json (the last active window first).</summary>
    public static List<string> VsCodeFolders(string? storageFile = null)
    {
        storageFile ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "globalStorage", "storage.json");
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(storageFile));
            var windows = json?["windowsState"];
            var list = new List<JsonNode?> { windows?["lastActiveWindow"] };
            if (windows?["openedWindows"] is JsonArray opened) list.AddRange(opened);
            return list.Select(w => w?["folder"]?.ToString()).OfType<string>()
                .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null).OfType<string>()
                .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return []; }
    }

    private (string? path, bool unsaved, string? problem) Resolve(string path, bool it)
    {
        if (path.Length > 0) return (Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)), false, null);
        if (ActiveFile() is not { } active)
            return (null, false, it ? "Non trovo il file aperto in VS Code: è aperto? Dimmi il nome del file." : "No file open in VS Code found: is it open? Tell me the file name.");
        return (active.path, active.unsaved, null);
    }

    private string? Refusal(string path, bool it)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !allowed(path))
            return it ? "Lavoro solo sui file di codice nella tua cartella utente, non nelle cartelle di sistema." : "I only work on code files in your user folder.";
        if (!CodeExtensions.Contains(Path.GetExtension(path))) return it ? $"{Path.GetFileName(path)} non è un file di codice." : $"{Path.GetFileName(path)} is not a code file.";
        return null;
    }

    public async Task<string> ReadAsync(string path, int? from, int? to, bool it, CancellationToken ct)
    {
        var (file, unsaved, problem) = Resolve(path, it);
        if (file is null) return problem!;
        if (Refusal(file, it) is string no) return no;
        string text;
        try { text = await File.ReadAllTextAsync(file, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return it ? $"Non riesco a leggere {file}." : $"Cannot read {file}."; }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int first = Math.Max(1, from ?? 1), last = Math.Min(Math.Min(lines.Length, to ?? lines.Length), first + 599);
        if (first > last) return it ? $"{Path.GetFileName(file)} ha {lines.Length} righe." : $"{Path.GetFileName(file)} has {lines.Length} lines.";
        string body = string.Join("\n", Enumerable.Range(first, last - first + 1).Select(n => $"{n,4}  {lines[n - 1]}"));
        string head = it ? $"File {file}, {lines.Length} righe" : $"File {file}, {lines.Length} lines";
        if (last < lines.Length || first > 1) head += it ? $", mostrate {first}–{last}" : $", shown {first}–{last}";
        if (unsaved) head += it ? ". ATTENZIONE: in VS Code ha modifiche non salvate, qui c'è la versione salvata" : ". NOTE: unsaved changes in VS Code; this is the saved version";
        return Security.External(it ? "codice" : "code", head + ":\n" + body);
    }

    public async Task<string> EditAsync(string path, string content, string mode, bool it, CancellationToken ct)
    {
        var (file, unsaved, problem) = Resolve(path, it);
        if (file is null) return problem!;
        if (Refusal(file, it) is string no) return no;
        if (unsaved) return it ? $"{Path.GetFileName(file)} ha modifiche non salvate in VS Code: chiedi all'utente di salvare (Ctrl+S), poi rileggi il file."
                               : $"{Path.GetFileName(file)} has unsaved changes in VS Code: ask the user to save (Ctrl+S), then read it again.";
        string old;
        try { old = await File.ReadAllTextAsync(file, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return it ? $"Non riesco a leggere {file}." : $"Cannot read {file}."; }
        // The file's own line endings are kept.
        bool crlf = old.Contains("\r\n");
        string fresh = content.Replace("\r\n", "\n");
        string oldPlain = old.Replace("\r\n", "\n");
        if (oldPlain.EndsWith('\n') && !fresh.EndsWith('\n')) fresh += "\n";
        if (fresh == oldPlain) return it ? "Nessuna differenza: il file resta com'è." : "No difference: the file stays as it is.";
        string ext = Path.GetExtension(file);
        if (mode == "comments")
        {
            var before = CodeLines(oldPlain, ext);
            var after = CodeLines(fresh, ext);
            if (!before.SequenceEqual(after))
            {
                int index = Enumerable.Range(0, Math.Min(before.Count, after.Count)).FirstOrDefault(i => before[i] != after[i], Math.Min(before.Count, after.Count));
                string line = index < before.Count ? before[index].Trim() : "(end of file)";
                return $"Refused: in comments mode the code must stay identical, indentation included, but it changes near \"{(line.Length > 60 ? line[..60] : line)}\". Read the file again and only add comments.";
            }
        }
        if (ext.Equals(".py", StringComparison.OrdinalIgnoreCase) && await PythonErrorAsync(fresh, ct) is string error)
            return it ? $"Rifiutato: il nuovo codice Python non è valido ({error}). Il file non è stato toccato." : $"Refused: the new Python code is not valid ({error}). The file was not touched.";
        // The milliseconds keep two quick edits apart; the path's mark keeps apart files with the same name in other projects.
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss.fff", CultureInfo.InvariantCulture);
        string backup = Path.Combine(backupFolder, $"{stamp}{BackupSuffix(file)}");
        try
        {
            Directory.CreateDirectory(backupFolder);
            // The bytes as they were (encoding and BOM included), not the decoded text.
            File.Copy(file, backup, false);
            string temp = file + ".al76tmp";
            await File.WriteAllTextAsync(temp, crlf ? fresh.Replace("\n", "\r\n") : fresh, new System.Text.UTF8Encoding(HasBom(file)), ct);
            File.Move(temp, file, true);   // in one go: VS Code reloads a saved file by itself
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return it ? $"Scrittura non riuscita: {e.Message}. Il file non è stato toccato." : $"Write failed: {e.Message}. The file was not touched.";
        }
        int delta = fresh.Split('\n').Length - oldPlain.Split('\n').Length;
        return it ? $"Salvato {Path.GetFileName(file)} ({(mode == "comments" ? "solo commenti" : "riscritto")}, {(delta >= 0 ? "+" : "")}{delta} righe). Copia di prima: {Path.GetFileName(backup)}. Per tornare indietro: restore_code."
                  : $"Saved {Path.GetFileName(file)} ({(mode == "comments" ? "comments only" : "rewritten")}, {(delta >= 0 ? "+" : "")}{delta} lines). Previous copy: {Path.GetFileName(backup)}. To undo: restore_code.";
    }

    /// <summary>Puts back the last copy of that file (or of the file open in VS Code).</summary>
    public async Task<string> RestoreAsync(string path, bool it, CancellationToken ct)
    {
        var (file, _, problem) = Resolve(path, it);
        if (file is null) return problem!;
        if (Refusal(file, it) is string no) return no;
        string? last = Directory.Exists(backupFolder)
            ? Directory.EnumerateFiles(backupFolder).Where(f => Path.GetFileName(f).EndsWith(BackupSuffix(file), StringComparison.OrdinalIgnoreCase)).Order().LastOrDefault()
            : null;
        if (last is null) return it ? $"Non ho copie di {Path.GetFileName(file)}." : $"No copies of {Path.GetFileName(file)}.";
        try
        {
            File.Copy(last, file, true);
            File.Delete(last);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (it ? "Ripristino non riuscito: " : "Restore failed: ") + e.Message; }
        return it ? $"Rimesso {Path.GetFileName(file)} com'era prima dell'ultima modifica." : $"{Path.GetFileName(file)} is back as it was before the last change.";
    }

    /// <summary>" &lt;8 hex of the full path&gt; &lt;name&gt;": the backups of this file and of no other.</summary>
    private static string BackupSuffix(string file)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(file).ToLowerInvariant()));
        return $" {Convert.ToHexString(hash, 0, 4).ToLowerInvariant()} {Path.GetFileName(file)}";
    }

    private static bool HasBom(string file)
    {
        try
        {
            using var s = File.OpenRead(file);
            Span<byte> b = stackalloc byte[3];
            return s.Read(b) == 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
        }
        catch (IOException) { return false; }
    }

    /// <summary>The lines that are code, exactly as written (indentation included): comments and blank lines removed.</summary>
    public static List<string> CodeLines(string text, string ext)
    {
        string marker = HashComments.Contains(ext) ? "#" : "//";
        var result = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            string code = WithoutComment(line, marker).TrimEnd(' ', '\t');
            if (code.Trim().Length > 0) result.Add(code);
        }
        return result;
    }

    /// <summary>The line up to its comment, ignoring markers inside quotes.</summary>
    private static string WithoutComment(string line, string marker)
    {
        char? quote = null;
        bool escaped = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (escaped) escaped = false;
            else if (c == '\\') escaped = true;
            else if (quote is char q) { if (c == q) quote = null; }
            else if (c is '"' or '\'') quote = c;
            else if (string.CompareOrdinal(line, i, marker, 0, marker.Length) == 0) return line[..i];
        }
        return line;
    }

    /// <summary>Python's own parser on the new text, when Python is installed ("py" launcher or python); null when valid or unknown.</summary>
    private async Task<string?> PythonErrorAsync(string code, CancellationToken ct)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"al76-check-{Guid.NewGuid():N}.py");
        try
        {
            await File.WriteAllTextAsync(temp, code, ct);
            foreach (var exe in new[] { "py", "python" })
            {
                try
                {
                    var r = await shell.RunAsync(exe, ["-I", "-c", "import ast,sys; ast.parse(open(sys.argv[1], encoding='utf-8').read())", temp], TimeSpan.FromSeconds(15), ct);
                    if (r.code == 0) return null;
                    if (r.error.Contains("SyntaxError") || r.error.Contains("Error"))
                        return r.error.Trim().Split('\n').LastOrDefault() ?? "syntax error";
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
            return null;
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }
}
