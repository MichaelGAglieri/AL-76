using System.Globalization;
using System.Text;
using AL76.Core.Diagnostics;
using AL76.Core.Safety;
using AL76.Core.Voice;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using static AL76.Core.Tools.ToolDef;

namespace AL76.Core.Tools;

/// <summary>
/// Files and PDFs (Mac: search_files, open_path, rename_file, merge_pdfs, extract_pdf_pages, pdf_info): found by a few
/// words of the name, never by a guessed path; renamed and moved without ever replacing a file; PDFs merged and cut
/// in under a second, without opening an app. System folders and private data (passwords, keys) stay closed.
/// </summary>
public static class FileTools
{
    public static IEnumerable<ToolDef> Create(IPcActions pc, FileSearch search)
    {
        static string L(ToolContext c, string it, string en) => c.Italian ? it : en;

        yield return new ToolDef
        {
            Name = "search_files",
            Description = "Search files and folders (the user's folders: Desktop, Documents, Downloads, Pictures, Music, Videos, OneDrive, and other drives) by a few words of their name (tolerant to case, underscores and small typos). Use it before open_path when you don't know the exact path; never guess paths.",
            Properties = new() { ["query"] = Prop("string", "A few words of the name, e.g. chimica organica") },
            Required = ["query"],
            Run = async (a, c, ct) =>
            {
                string q = a.Str("query");
                if (q.Length == 0) return ToolOutcome.Ok(L(c, "Ricerca vuota.", "Empty search."));
                var found = await Task.Run(() => search.Find(q, 10, ct), ct);
                return ToolOutcome.Ok(found.Count == 0 ? L(c, $"Nessun file trovato per {q}.", $"No file found for {q}.")
                    : L(c, "File trovati (il primo è il più simile):\n", "Files found (the first is the closest):\n") + string.Join("\n", found));
            },
        };
        yield return new ToolDef
        {
            Name = "list_folder",
            Description = "List what a folder contains (subfolders and files with sizes): \"cosa c'è nella cartella X\", \"che file ho sul desktop\". Give a path or a few words of the folder's name.",
            Properties = new() { ["folder"] = Prop("string", "Path, or a few words of the folder's name, or desktop / documenti / download / immagini.") },
            Required = ["folder"],
            Run = async (a, c, ct) =>
            {
                string? folder = await Task.Run(() => search.FindFolder(a.Str("folder"), ct), ct);
                if (folder is null) return ToolOutcome.Ok(L(c, $"Non trovo una cartella che si chiami {a.Str("folder")}.", $"No folder called {a.Str("folder")}."));
                return ToolOutcome.Ok(search.List(folder, 80, c.Italian));
            },
        };
        yield return new ToolDef
        {
            Name = "open_path",
            Description = "Open a file or folder with its usual app, or show it in File Explorer.",
            Properties = new()
            {
                ["path"] = Prop("string", "Exact path from search_files."),
                ["reveal"] = Prop("boolean", "Only if the user asks to show it in File Explorer; otherwise the file is opened."),
            },
            Required = ["path"],
            Run = async (a, c, ct) =>
            {
                string? path = Resolve(a.Str("path"), search);
                if (path is null) return ToolOutcome.Ok(L(c, $"Il percorso non esiste: {a.Str("path")}. Usa search_files con alcune parole del nome.",
                                                              $"The path does not exist: {a.Str("path")}. Use search_files with a few words of the name."));
                if (!search.Allowed(path)) return ToolOutcome.Ok(L(c, "Questa è una cartella di sistema o con dati privati (password, chiavi, impostazioni di Windows): non la apro.",
                                                                     "That is a system folder or holds private data (passwords, keys, Windows settings): not opening it."));
                bool reveal = a.Bool("reveal") == true;
                // A program, a script or an installer runs code: always the yes first.
                if (!reveal && Security.IsExecutable(path))
                    return new ToolOutcome.NeedsYes(new PendingAction(L(c, $"Avviare il programma {Path.GetFileName(path)}", $"Run the program {Path.GetFileName(path)}"),
                        Risk.RunProgram, async ct2 => (await pc.OpenPathAsync(path, false, ct2)).ForModel));
                var r = await pc.OpenPathAsync(path, reveal, ct);
                return r.Ok ? ToolOutcome.Ok(L(c, $"Aperto {path}", $"Opened {path}")) : ToolOutcome.Error(r.ForModel);
            },
        };
        yield return new ToolDef
        {
            Name = "rename_file",
            Description = "Rename a file and/or move it to another folder, created if missing (never over an existing file).",
            Properties = new()
            {
                ["path"] = Prop("string", "Full path."),
                ["new_name"] = Prop("string", "New name (the extension is kept); omit to only move."),
                ["folder"] = Prop("string", "Destination folder; omit to only rename."),
            },
            Required = ["path"],
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(Rename(a.Str("path"), a.Str("new_name"), a.Str("folder"), search, c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "pdf_info",
            Description = "Pages, size and title of a PDF.",
            Properties = new() { ["file"] = Prop("string", "Full path.") },
            Required = ["file"],
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(PdfInfo(a.Str("file"), search, c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "merge_pdfs",
            Description = "Combine PDFs (and JPEG or PNG images) into ONE new PDF in the given order, in under a second, without any app. Find the paths first with search_files.",
            Properties = new()
            {
                ["files"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "array", ["items"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" }, ["description"] = "Full paths, in order." },
                ["name"] = Prop("string", "Name of the new PDF."),
                ["folder"] = Prop("string", "Where to save it; default the folder of the first file."),
                ["show"] = Prop("boolean", "Show it in File Explorer, default true."),
            },
            Required = ["files"],
            Run = async (a, c, ct) =>
            {
                var files = a["files"] is System.Text.Json.Nodes.JsonArray arr ? arr.Select(n => n?.ToString() ?? "").Where(s => s.Length > 0).ToList() : [];
                var (text, made) = Merge(files, a.Str("name"), a.Str("folder"), search, c.Italian);
                if (made is not null && a.Bool("show") != false) await pc.OpenPathAsync(made, true, ct);
                return ToolOutcome.Ok(text);
            },
        };
        yield return new ToolDef
        {
            Name = "extract_pdf_pages",
            Description = "Save some pages of a PDF as a new PDF (\"pagine 1-3, 5\").",
            Properties = new()
            {
                ["file"] = Prop("string", "Full path."),
                ["pages"] = Prop("string", "e.g. 1-3, 5"),
                ["name"] = Prop("string", "Name of the new PDF."),
                ["folder"] = Prop("string", "Where to save it."),
            },
            Required = ["file", "pages"],
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(Extract(a.Str("file"), a.Str("pages"), a.Str("name"), a.Str("folder"), search, c.Italian))),
        };
    }

    /// <summary>The path as given, or the most similar existing one (the model rebuilt it from memory).</summary>
    public static string? Resolve(string raw, FileSearch search)
    {
        string path = Expand(raw);
        if (path.Length == 0) return null;
        if (File.Exists(path) || Directory.Exists(path)) return Path.GetFullPath(path);
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (name.Length == 0) return null;
        // Same folder, different case or spacing.
        string? folder = Path.GetDirectoryName(path);
        if (folder is not null && Directory.Exists(folder))
        {
            string key = FileSearch.Key(name);
            var near = Directory.EnumerateFileSystemEntries(folder).FirstOrDefault(e => FileSearch.Key(Path.GetFileName(e)) == key);
            if (near is not null) return near;
        }
        return search.Find(Path.GetFileNameWithoutExtension(name), 1).FirstOrDefault(p => FileSearch.Key(Path.GetFileName(p)) == FileSearch.Key(name));
    }

    /// <summary>"~\Desktop\x", "%USERPROFILE%\x" and quotes, as the model writes them.</summary>
    public static string Expand(string raw)
    {
        string p = raw.Trim().Trim('"', '\'');
        if (p.StartsWith('~')) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p[1..];
        return Environment.ExpandEnvironmentVariables(p);
    }

    public static string Rename(string rawPath, string newName, string folder, FileSearch search, bool it)
    {
        string L(string i, string e) => it ? i : e;
        if (Resolve(rawPath, search) is not string path || !File.Exists(path) && !Directory.Exists(path))
            return L($"Non trovo {rawPath}: usa search_files.", $"Cannot find {rawPath}: use search_files.");
        if (!search.Allowed(path)) return L("Questo file è in una cartella protetta: non lo sposto.", "That file is in a protected folder: not moving it.");
        bool isDir = Directory.Exists(path);
        string ext = isDir ? "" : Path.GetExtension(path);
        string name = newName.Trim();
        if (name.Length == 0) name = Path.GetFileName(path);
        else
        {
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
                return L("Il nuovo nome contiene caratteri non permessi.", "The new name has characters that are not allowed.");
            if (!isDir && !name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name += ext;   // the extension is kept
        }
        string target = folder.Trim().Length == 0 ? Path.GetDirectoryName(path)! : Expand(folder);
        if (!Path.IsPathRooted(target)) target = Path.Combine(Path.GetDirectoryName(path)!, target);
        if (!search.Allowed(target)) return L("Quella cartella è protetta: non ci sposto file.", "That folder is protected: not moving files there.");
        string dest = Path.Combine(target, name);
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(path), StringComparison.Ordinal)) return L("Il nome è già quello.", "It already has that name.");
        if (File.Exists(dest) || Directory.Exists(dest) && !string.Equals(Path.GetFullPath(dest), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            return L($"Esiste già {dest}: non lo sovrascrivo. Scegli un altro nome.", $"{dest} already exists: not overwriting it. Pick another name.");
        try
        {
            Directory.CreateDirectory(target);
            if (isDir) Directory.Move(path, dest); else File.Move(path, dest);
            Log.Audit("file rinominato", $"{path} → {dest}");
            return L($"Fatto: {dest}", $"Done: {dest}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return L($"Non riesco: {e.Message}", $"Could not: {e.Message}"); }
    }

    public static string PdfInfo(string raw, FileSearch search, bool it)
    {
        if (Resolve(raw, search) is not string path || !File.Exists(path)) return it ? $"Non trovo {raw}." : $"Cannot find {raw}.";
        try
        {
            using var doc = PdfDocument.Open(path);
            string title = doc.Information.Title?.Trim() ?? "";
            double mb = new FileInfo(path).Length / 1048576.0;
            return it ? $"{Path.GetFileName(path)}: {doc.NumberOfPages} pagine, {mb:0.0} MB{(title.Length > 0 ? $", titolo «{title}»" : "")}."
                      : $"{Path.GetFileName(path)}: {doc.NumberOfPages} pages, {mb:0.0} MB{(title.Length > 0 ? $", title \"{title}\"" : "")}.";
        }
        catch (Exception e) { return it ? $"Non riesco a leggere il PDF: {e.Message}" : $"Cannot read the PDF: {e.Message}"; }
    }

    /// <summary>"1-3, 5" → 1,2,3,5 (1-based, in order, no repeats); null if something does not fit the document.</summary>
    public static List<int>? Pages(string spec, int count)
    {
        var pages = new List<int>();
        foreach (var part in spec.Replace(';', ',').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Replace('–', '-').Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length is < 1 or > 2 || !int.TryParse(ends[0], out int from)) return null;
            int to = from;
            if (ends.Length == 2 && !int.TryParse(ends[1], out to)) return null;
            if (from < 1 || to < from || to > count) return null;
            for (int p = from; p <= to; p++) if (!pages.Contains(p)) pages.Add(p);
        }
        return pages.Count == 0 ? null : pages;
    }

    public static string Extract(string raw, string spec, string name, string folder, FileSearch search, bool it)
    {
        string L(string i, string e) => it ? i : e;
        if (Resolve(raw, search) is not string path || !File.Exists(path)) return L($"Non trovo {raw}.", $"Cannot find {raw}.");
        try
        {
            byte[] source = File.ReadAllBytes(path);
            int count;
            using (var doc = PdfDocument.Open(source)) count = doc.NumberOfPages;
            if (Pages(spec, count) is not { } pages) return L($"Pagine non valide: il PDF ne ha {count}.", $"Invalid pages: the PDF has {count}.");
            byte[] pdf = PdfMerger.Merge([source], [pages]);
            string dest = Save(pdf, name.Length > 0 ? name : $"{Path.GetFileNameWithoutExtension(path)} pagine {spec.Replace(" ", "")}",
                               folder.Length > 0 ? Expand(folder) : Path.GetDirectoryName(path)!, search);
            Log.Audit("pdf: pagine estratte", $"{path} [{spec}] → {dest}");
            return L($"Salvato {dest} ({pages.Count} pagine).", $"Saved {dest} ({pages.Count} pages).");
        }
        catch (Exception e) when (e is not OperationCanceledException) { return L($"Non riesco: {e.Message}", $"Could not: {e.Message}"); }
    }

    public static (string text, string? made) Merge(List<string> raw, string name, string folder, FileSearch search, bool it)
    {
        string L(string i, string e) => it ? i : e;
        if (raw.Count == 0) return (L("Nessun file da unire.", "No files to merge."), null);
        var paths = new List<string>();
        foreach (var r in raw)
        {
            if (Resolve(r, search) is not string p || !File.Exists(p)) return (L($"Non trovo {r}: usa search_files.", $"Cannot find {r}: use search_files."), null);
            paths.Add(p);
        }
        try
        {
            var parts = new List<byte[]>();
            foreach (var p in paths)
            {
                string ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext == ".pdf") parts.Add(File.ReadAllBytes(p));
                else if (ext is ".jpg" or ".jpeg" or ".png") parts.Add(ImagePage(File.ReadAllBytes(p), ext == ".png"));
                else return (L($"{Path.GetFileName(p)} non è un PDF né un'immagine JPEG o PNG.", $"{Path.GetFileName(p)} is neither a PDF nor a JPEG or PNG image."), null);
            }
            byte[] pdf = PdfMerger.Merge(parts, null);
            int pages;
            using (var doc = PdfDocument.Open(pdf)) pages = doc.NumberOfPages;
            string dest = Save(pdf, name.Length > 0 ? name : $"{Path.GetFileNameWithoutExtension(paths[0])} uniti",
                               folder.Length > 0 ? Expand(folder) : Path.GetDirectoryName(paths[0])!, search);
            Log.Audit("pdf: uniti", $"{paths.Count} file → {dest}");
            return (L($"Creato {dest}: {paths.Count} file, {pages} pagine.", $"Created {dest}: {paths.Count} files, {pages} pages."), dest);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return (L($"Non riesco a unire: {e.Message}", $"Could not merge: {e.Message}"), null); }
    }

    /// <summary>One A4-wide page holding the image at its own proportions.</summary>
    public static byte[] ImagePage(byte[] image, bool png)
    {
        var (w, h) = ImageSize(image, png) ?? (595, 842);
        double pageW = 595, pageH = pageW * h / w;
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(pageW, pageH);
        var box = new PdfRectangle(0, 0, pageW, pageH);
        if (png) page.AddPng(image, box); else page.AddJpeg(image, box);
        return builder.Build();
    }

    /// <summary>Pixel size from the PNG header or the JPEG frame marker.</summary>
    public static (int w, int h)? ImageSize(byte[] b, bool png)
    {
        if (png) return b.Length > 24 ? ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]) : null;
        for (int i = 2; i + 9 < b.Length;)
        {
            if (b[i] != 0xFF) { i++; continue; }
            byte marker = b[i + 1];
            int length = (b[i + 2] << 8) | b[i + 3];
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
            i += 2 + length;
        }
        return null;
    }

    /// <summary>Writes the PDF with a free name ("x.pdf", "x 2.pdf"…): never over an existing file.</summary>
    private static string Save(byte[] pdf, string name, string folder, FileSearch search)
    {
        if (!search.Allowed(folder)) throw new IOException("cartella protetta");
        Directory.CreateDirectory(folder);
        string clean = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        if (clean.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) clean = clean[..^4];
        if (clean.Length == 0) clean = "documento";
        string dest = Path.Combine(folder, clean + ".pdf");
        for (int n = 2; File.Exists(dest); n++) dest = Path.Combine(folder, $"{clean} {n}.pdf");
        File.WriteAllBytes(dest, pdf);
        return dest;
    }
}

/// <summary>
/// Finds files by a few words of the name in the user's folders. Tolerant: case, accents, underscores, dashes and a
/// small typo per word. Skips system folders, hidden ones and the big developer ones (node_modules, .git).
/// </summary>
public sealed class FileSearch(Func<IEnumerable<string>> roots, Func<string, bool>? protectedPath = null)
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", "bin", "obj", "__pycache__", ".venv", "venv", "$recycle.bin", "system volume information",
        "windows", "program files", "program files (x86)", "programdata", "appdata", "library", ".cache", ".nuget", ".dotnet",
    };

    /// <summary>The user's folders on this PC (Windows: profile folders and OneDrive), for the app.</summary>
    public static IEnumerable<string> UserRoots()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(home, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            home,
        };
        foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } od) list.Add(od);
        // The other drives (D:, a USB key): files are often kept there. The system drive is covered by the profile.
        string system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "";
        try
        {
            foreach (var d in DriveInfo.GetDrives())
                if (d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable && !d.Name.Equals(system, StringComparison.OrdinalIgnoreCase))
                    list.Add(d.RootDirectory.FullName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return list.Where(p => p.Length > 0 && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Lowercase, no accents, underscores and dashes as spaces, single spaces.</summary>
    public static string Key(string name)
    {
        var sb = new StringBuilder();
        foreach (char ch in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) || ch == '.' ? char.ToLowerInvariant(ch) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Not a system folder nor private data (credentials, keys, browser profiles).</summary>
    public bool Allowed(string path)
    {
        string p = Path.GetFullPath(path).ToLowerInvariant().Replace('/', '\\');
        string[] closed =
        [
            "\\windows\\", "\\program files", "\\programdata\\", "\\appdata\\roaming\\microsoft\\", "\\appdata\\local\\microsoft\\",
            "\\.ssh", "\\.gnupg", "\\.aws", "\\.azure", "\\.kube", "\\appdata\\local\\google\\chrome\\user data", "\\appdata\\local\\microsoft\\edge\\user data",
            "\\appdata\\roaming\\mozilla\\firefox\\profiles", "\\appdata\\roaming\\al-76\\", "\\appdata\\local\\al-76\\",
        ];
        if (closed.Any(c => (p + "\\").Contains(c))) return false;
        return protectedPath?.Invoke(path) != true;
    }

    /// <summary>The best matches, most similar first. Stops after a few seconds or 200,000 entries.</summary>
    public List<string> Find(string query, int max, CancellationToken ct = default)
    {
        var words = Key(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        var found = new List<(string path, int score)>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int seen = 0;
        var queue = new Queue<(string dir, int depth)>(roots().Select(r => (r, 0)));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0 && clock.Elapsed < TimeSpan.FromSeconds(8) && seen < 300_000)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            if (!visited.Add(dir) || !Allowed(dir)) continue;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
            foreach (var entry in entries)
            {
                seen++;
                string name = Path.GetFileName(entry);
                if (name.StartsWith('.') || name.StartsWith('~')) continue;
                int score = Score(Key(name), words);
                if (score > 0) found.Add((entry, score - depth));
                bool folder;
                try { folder = Directory.Exists(entry) && !IsLink(entry); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (folder && depth < 8 && !Skipped.Contains(name)) queue.Enqueue((entry, depth + 1));
            }
        }
        return found.OrderByDescending(f => f.score).ThenBy(f => f.path.Length).Select(f => f.path)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    /// <summary>A symbolic link or junction (skipped: loops, other places). OneDrive's folders are reparse points too
    /// (cloud placeholders), but they are real folders: a tester saw none of the subfolders of a Desktop in OneDrive (23/11).</summary>
    public static bool IsLink(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null;
    }

    /// <summary>A folder by path or by a few words of its name: the path as given, else the best folder found.</summary>
    public string? FindFolder(string pathOrName, CancellationToken ct = default)
    {
        string p = FileTools.Expand(pathOrName);
        if (p.Length > 0 && Path.IsPathRooted(p) && Directory.Exists(p)) return Path.GetFullPath(p);
        string spoken = p.ToLowerInvariant() switch
        {
            "desktop" or "scrivania" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "documenti" or "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "download" or "downloads" or "scaricati" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "immagini" or "pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            _ => "",
        };
        if (spoken.Length > 0 && Directory.Exists(spoken)) return spoken;
        return Find(Path.GetFileName(p.TrimEnd('\\', '/')), 20, ct).FirstOrDefault(Directory.Exists);
    }

    /// <summary>What a folder holds: subfolders first, then files with their size; at most <paramref name="max"/> entries.</summary>
    public string List(string folder, int max, bool it)
    {
        if (!Allowed(folder)) return it ? "Questa è una cartella di sistema o con dati privati: non la leggo." : "That is a system or private folder: not reading it.";
        List<string> dirs, files;
        try
        {
            var all = Directory.EnumerateFileSystemEntries(folder).Where(e => !Path.GetFileName(e).StartsWith('.') && !Path.GetFileName(e).StartsWith('~')
                                                                            && !Path.GetFileName(e).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)).ToList();
            dirs = all.Where(Directory.Exists).OrderBy(e => e, StringComparer.CurrentCultureIgnoreCase).ToList();
            files = all.Except(dirs).OrderBy(e => e, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (it ? "Non riesco a leggere la cartella: " : "Cannot read the folder: ") + e.Message; }
        var sb = new StringBuilder();
        sb.Append(it ? $"{folder}: {dirs.Count} cartelle, {files.Count} file.\n" : $"{folder}: {dirs.Count} folders, {files.Count} files.\n");
        int shown = 0;
        foreach (var d in dirs.Take(max)) { sb.Append("[cartella] ").Append(Path.GetFileName(d)).Append('\n'); shown++; }
        foreach (var f in files.Take(Math.Max(0, max - shown)))
        {
            long size = 0;
            try { size = new FileInfo(f).Length; } catch (IOException) { }
            sb.Append(Path.GetFileName(f)).Append(size >= 1048576 ? $" ({size / 1048576.0:0.0} MB)" : size > 0 ? $" ({Math.Max(1, size / 1024)} KB)" : "").Append('\n');
            shown++;
        }
        if (dirs.Count + files.Count > shown) sb.Append(it ? $"…e altri {dirs.Count + files.Count - shown}." : $"…and {dirs.Count + files.Count - shown} more.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>0 when some word is missing; more for exact words, whole-name matches and fewer extra words.</summary>
    public static int Score(string key, string[] words)
    {
        var parts = key.Split(' ', '.');
        int score = 0;
        foreach (var w in words)
        {
            if (parts.Contains(w)) score += 30;
            else if (parts.Any(p => p.StartsWith(w))) score += 20;
            else if (key.Contains(w)) score += 12;
            else if (w.Length >= 4 && parts.Any(p => Math.Abs(p.Length - w.Length) <= 1 && TextTools.Distance(p, w) <= 1)) score += 8;
            else return 0;
        }
        string bare = Path.GetFileNameWithoutExtension(key);
        if (bare == string.Join(' ', words)) score += 40;
        return score + 20 - Math.Min(20, parts.Length);
    }
}
