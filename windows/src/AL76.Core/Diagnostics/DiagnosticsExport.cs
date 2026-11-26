using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AL76.Core.Config;

namespace AL76.Core.Diagnostics;

/// <summary>
/// "Esporta diagnostica": one zip to send to whoever helps, without personal data. Inside: the PC and what AL-76
/// measured, the technical settings (switches, numbers, chosen engines; no names, places, addresses, ids or
/// folders), the models present, and diagnostica.log with every quoted phrase, address, e-mail, user folder and
/// the user's own words taken out. sicurezza.log (what was done in which app) never goes in.
/// </summary>
public static partial class DiagnosticsExport
{
    /// <summary>String settings that say how AL-76 works, never who the user is.</summary>
    private static readonly HashSet<string> SafeStrings = new(StringComparer.OrdinalIgnoreCase)
    {
        "Language", "Brain", "BrainModel", "LocalModel", "Profile", "VoiceEngine", "KokoroVoice", "Nucleus", "NucleusStyle",
        "RobotFlames", "Theme", "WakeModel", "CommandModel", "Tier",
    };

    public static string Create(string zipPath, string dataDir, string modelsDir, AppConfig config, string systemReport, string version, DateTime now)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            w.Write(text);
        }
        Add("leggimi.txt",
            $"AL-76 {version} per Windows · diagnostica del {now:yyyy-MM-dd HH:mm}\n\n" +
            "sistema.txt: il PC, il livello scelto da AL-76, i modelli presenti.\n" +
            "impostazioni.json: solo interruttori, numeri e motori scelti (niente nomi, luoghi, indirizzi, ID o cartelle).\n" +
            "diagnostica.log: le ultime righe, senza frasi dette o scritte, indirizzi, e-mail e cartelle personali.\n" +
            "Il registro delle azioni (sicurezza.log) non è incluso.\n");
        var models = Directory.Exists(modelsDir)
            ? Directory.GetFiles(modelsDir).Select(f => $"- {Path.GetFileName(f)} ({new FileInfo(f).Length / 1_048_576.0:0} MB)")
            : ["- nessuno"];
        Add("sistema.txt", Scrub(systemReport, config) + $"\n\nWindows: {Environment.OSVersion.VersionString}\n.NET: {Environment.Version}\n" +
                           $"Processo a 64 bit: {Environment.Is64BitProcess}\n\nModelli:\n{string.Join("\n", models)}\n");
        Add("impostazioni.json", SafeConfig(config).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        string log = Path.Combine(dataDir, "diagnostica.log");
        if (File.Exists(log))
        {
            var lines = ReadShared(log).Split('\n').TakeLast(4000).Select(l => Scrub(l.TrimEnd('\r'), config));
            Add("diagnostica.log", string.Join("\n", lines));
        }
        return zipPath;
    }

    /// <summary>The log while AL-76 is still writing it.</summary>
    private static string ReadShared(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(f, Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static JsonObject SafeConfig(AppConfig config)
    {
        var all = JsonSerializer.SerializeToNode(config) as JsonObject ?? [];
        var safe = new JsonObject();
        foreach (var (key, value) in all)
        {
            if (value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number)
                safe[key] = v.DeepClone();
            else if (value is JsonValue s && s.GetValueKind() == JsonValueKind.String && SafeStrings.Contains(key))
                safe[key] = s.DeepClone();
            else if (key == nameof(AppConfig.EnabledModules))
                safe[key] = value?.DeepClone();
            else if (value is JsonValue t && t.GetValueKind() == JsonValueKind.String)
                safe[key] = t.ToString().Length == 0 ? "" : "(impostato)";
            else if (value is JsonArray list)
                safe[key] = $"({list.Count} elementi)";
        }
        return safe;
    }

    [GeneratedRegex(@"«[^»]*»")] private static partial Regex Quoted();
    [GeneratedRegex("\"[^\"]{2,}\"")] private static partial Regex DoubleQuoted();
    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+")] private static partial Regex Email();
    /// <summary>"C:\Users\name" and, inside JSON or quoted command lines, "C:\\Users\\name" (a tester's log, 26/11).</summary>
    [GeneratedRegex(@"(?i)([a-z]:(?:\\{1,2})Users(?:\\{1,2}))[^\\\s""]+")] private static partial Regex UserFolder();
    /// <summary>Below the user's folder only its first part stays ("Desktop", "AppData"): the names of the user's own
    /// folders and files are not for the zip.</summary>
    [GeneratedRegex(@"(?i)(Users(?:\\{1,2})…(?:\\{1,2})[^\\\s""]+)(?:\\{1,2})[^\n""]*")] private static partial Regex UserFolderTail();
    /// <summary>A brain's tool call: "cervello: $ app_type app=WhatsApp text=…" keeps the tool and the argument names.</summary>
    [GeneratedRegex(@"^(\S+ \S+ cervello: \$ \S+)(.*)$")] private static partial Regex ToolCall();
    [GeneratedRegex(@"(\w+)=.*?(?=\s\w+=|$)")] private static partial Regex ToolArgument();
    /// <summary>A tool's answer: only its first words (a folder listing, a message, a contact are the user's).</summary>
    [GeneratedRegex(@"^(\S+ \S+ cervello: [←✕] )(.*)$")] private static partial Regex ToolAnswer();
    [GeneratedRegex(@"(?i)(/Users/|/home/)[^/\s]+")] private static partial Regex UnixHome();
    [GeneratedRegex(@"\b(?!127\.)(\d{1,3}\.){3}\d{1,3}\b")] private static partial Regex Ipv4();
    [GeneratedRegex(@"(?i)\b(Bearer|token|code|password|access_token|refresh_token)([=: ]+)\S+")] private static partial Regex Secret();
    [GeneratedRegex(@"(?i)^(\S+ \S+ )(iniziativa|dev): .*$")] private static partial Regex UserLine();
    /// <summary>"voce: what was heard → note": the phrase is not in quotes.</summary>
    [GeneratedRegex(@"^(\S+ \S+ voce: ).*?( → )")] private static partial Regex HeardLine();

    /// <summary>One line without personal data: quoted text, e-mails, user folders, addresses, secrets, the user's own words.</summary>
    public static string Scrub(string line, AppConfig config)
    {
        string s = UserLine().Replace(line, "$1$2: …");
        s = HeardLine().Replace(s, "$1…$2");
        s = Quoted().Replace(s, "«…»");
        s = DoubleQuoted().Replace(s, "\"…\"");
        s = Email().Replace(s, "…@…");
        s = ToolCall().Replace(s, m => m.Groups[1].Value + ToolArgument().Replace(m.Groups[2].Value, "$1=…"));
        s = ToolAnswer().Replace(s, m => m.Groups[1].Value + (m.Groups[2].Value.Length > 60 ? m.Groups[2].Value[..60] + "…" : m.Groups[2].Value));
        s = UserFolder().Replace(s, "$1…");
        s = UserFolderTail().Replace(s, "$1\\…");
        s = UnixHome().Replace(s, "$1…");
        s = Ipv4().Replace(s, "x.x.x.x");
        s = Secret().Replace(s, "$1$2…");
        var words = new[] { config.UserName, config.City, config.UniversityName, config.PrinterSerial }
            .Concat(config.Areas).Where(w => w.Trim().Length >= 3).Select(w => w.Trim());
        foreach (var w in words) s = Regex.Replace(s, $@"\b{Regex.Escape(w)}\b", "…", RegexOptions.IgnoreCase);
        return s;
    }
}
