using System.Text.Json;
using System.Text.Json.Serialization;

namespace AL76.Core.Config;

/// <summary>What the user is: it changes the first-run setup, the morning briefing and the initiative.</summary>
public enum UserProfile { Studente, Lavoratore, StudenteLavoratore, Altro }

/// <summary>
/// Everything personal is a setting here, nothing is wired to one person (the Windows version is for everyone).
/// Saved as config.json in %APPDATA%\AL-76. Missing keys take the defaults below.
/// </summary>
/// <summary>A weekly slot of an area: days 1 = Monday … 7 = Sunday, times "HH:mm" (To after From; no slot over midnight).</summary>
public sealed class AreaHours
{
    public string Area { get; set; } = "";
    public List<int> Days { get; set; } = [1, 2, 3, 4, 5];
    public string From { get; set; } = "09:00";
    public string To { get; set; } = "18:00";

    public bool Covers(DateTime t)
    {
        int day = t.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)t.DayOfWeek;
        return Days.Contains(day) && TimeSpan.TryParse(From, out var from) && TimeSpan.TryParse(To, out var to)
               && t.TimeOfDay >= from && t.TimeOfDay < to;
    }

    /// <summary>The area whose slot holds this moment (the first listed wins), or null outside every slot.</summary>
    public static string? At(IEnumerable<AreaHours> schedule, DateTime t) => schedule.FirstOrDefault(h => h.Area.Length > 0 && h.Covers(t))?.Area;

    private static readonly string[][] DayNames =
    [
        ["lun", "lunedì", "lunedi", "mon", "monday"], ["mar", "martedì", "martedi", "tue", "tuesday"],
        ["mer", "mercoledì", "mercoledi", "wed", "wednesday"], ["gio", "giovedì", "giovedi", "thu", "thursday"],
        ["ven", "venerdì", "venerdi", "fri", "friday"], ["sab", "sabato", "sat", "saturday"], ["dom", "domenica", "sun", "sunday"],
    ];

    private static int Day(string word) => Array.FindIndex(DayNames, names => names.Contains(word.Trim().ToLowerInvariant())) + 1;

    /// <summary>One line of the settings: "Lavoro: lun-ven 9-18", "Studio: lun, mer, ven 15:30-19", "Personale: tutti i giorni 19-23".</summary>
    public static AreaHours? Parse(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line.Trim(),
            @"^(?<area>[^:]+):\s*(?<days>.+?)\s+(?<from>\d{1,2}(?:[:.]\d{2})?)\s*[-–]\s*(?<to>\d{1,2}(?:[:.]\d{2})?)$");
        if (!m.Success) return null;
        string days = m.Groups["days"].Value.ToLowerInvariant().Replace('–', '-');
        var list = new List<int>();
        if (days is "tutti i giorni" or "ogni giorno" or "every day" or "daily") list.AddRange(Enumerable.Range(1, 7));
        else
            foreach (var part in days.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Where(p => p is not "e" and not "and"))
            {
                var range = part.Split('-');
                int a = Day(range[0]), b = range.Length == 2 ? Day(range[1]) : a;
                if (a == 0 || b == 0 || range.Length > 2) return null;
                for (int d = a; ; d = d % 7 + 1) { if (!list.Contains(d)) list.Add(d); if (d == b) break; }
            }
        static string? Time(string t)
        {
            var p = t.Replace('.', ':').Split(':');
            int h = int.Parse(p[0]), min = p.Length > 1 ? int.Parse(p[1]) : 0;
            return h is >= 0 and <= 24 && min is >= 0 and < 60 ? $"{h:00}:{min:00}" : null;
        }
        string? from = Time(m.Groups["from"].Value), to = Time(m.Groups["to"].Value);
        if (from is null || to is null || string.CompareOrdinal(to, from) <= 0 || list.Count == 0) return null;
        return new AreaHours { Area = m.Groups["area"].Value.Trim(), Days = list.Order().ToList(), From = from, To = to == "24:00" ? "23:59" : to };
    }

    /// <summary>The line shown in the settings, in the user's language.</summary>
    public string Format(bool italian)
    {
        string Name(int d) => DayNames[d - 1][italian ? 0 : (d == 6 || d == 7 ? 2 : 3)];
        var sorted = Days.Order().ToList();
        string days = sorted.Count == 7 ? (italian ? "tutti i giorni" : "every day")
            : sorted.Count > 2 && sorted[^1] - sorted[0] == sorted.Count - 1 ? $"{Name(sorted[0])}-{Name(sorted[^1])}"
            : string.Join(", ", sorted.Select(Name));
        static string Short(string t)
        {
            string hour = t[..2].TrimStart('0').PadLeft(1, '0');
            return t.EndsWith(":00") ? hour : hour + t[2..];
        }
        return $"{Area}: {days} {Short(From)}-{Short(To)}";
    }
}

/// <summary>Where an area's calendar and reminders live: "local" (files on this PC), "microsoft" or "google";
/// Calendar and TaskList are names in that account (empty = the default one).</summary>
public sealed class AreaSource
{
    public string Area { get; set; } = "";
    public string Provider { get; set; } = "local";
    public string Calendar { get; set; } = "";
    public string TaskList { get; set; } = "";
}

public sealed class AppConfig
{
    /// <summary>"it" or "en". Empty = the language of Windows (Italian if Windows is Italian, else English).</summary>
    public string Language { get; set; } = "";
    public string AssistantName { get; set; } = "AL-76";
    public string WakePhrase { get; set; } = DefaultWakePhrase;
    public string UserName { get; set; } = "";
    /// <summary>How to address the user: empty = no form of address.</summary>
    public string Honorific { get; set; } = "";
    public UserProfile Profile { get; set; } = UserProfile.Studente;
    /// <summary>City for the weather; empty = the place of the internet connection.</summary>
    public string City { get; set; } = "";
    /// <summary>A free line about the user, given to the brains ("Studio economia a Bologna, lavoro in un bar il sabato").</summary>
    public string About { get; set; } = "";
    /// <summary>What and where the user studies ("Economia, Università di Bologna"); empty if not a student.</summary>
    public string Study { get; set; } = "";
    /// <summary>The user's job and hours ("Impiegato in un'agenzia, 9–18"); empty if not working.</summary>
    public string Work { get; set; } = "";
    public string Interests { get; set; } = "";
    /// <summary>Reminders and calendar are split into these areas; "passa a lavoro" switches the active one.</summary>
    public List<string> Areas { get; set; } = ["Studio", "Lavoro", "Personale"];
    public string ActiveArea { get; set; } = "Studio";
    /// <summary>When each area starts by itself ("Lavoro, lun–ven 9–18"): at the start of a slot the active area
    /// switches; a switch by voice lasts until the next slot begins. Empty = only by voice.</summary>
    public List<AreaHours> AreaSchedule { get; set; } = [];
    /// <summary>Programs, scripts and forgetting memories ask Windows Hello after the "yes" (Mac: ownerCheck, Touch ID).</summary>
    public bool OwnerCheck { get; set; } = true;
    /// <summary>The voiceprint check: "off", "observe" (checks and logs, lets everything through; default) or "on"
    /// (other voices, the TV, people in the room are refused). Mac: voiceCheck.</summary>
    public string VoiceCheck { get; set; } = "observe";
    /// <summary>How similar to the recorded voice a phrase must be (Mac: voiceThreshold 0.6).</summary>
    public float VoiceThreshold { get; set; } = 0.6f;
    /// <summary>Where dictated brainstormings go; empty = Documenti\Brainstorming.</summary>
    public string BrainstormFolder { get; set; } = "";
    /// <summary>Where the user's projects are; empty = Projects in the user folder, source\repos, Documents\GitHub.</summary>
    public List<string> ProjectFolders { get; set; } = [];
    /// <summary>News feeds, one per line as "Nome | https://…"; empty = the default ones (technology, AI, robotics).</summary>
    public List<string> NewsSources { get; set; } = [];
    /// <summary>The user's own topics: news about them come first ("Formula 1", "medicina").</summary>
    public List<string> NewsTopics { get; set; } = [];
    /// <summary>The morning briefing also says the news.</summary>
    public bool BriefingNews { get; set; } = true;
    /// <summary>Free RAM below this percentage: the local model is taken out of memory (Mac: memoryUnloadBelow).</summary>
    public int MemoryUnloadBelow { get; set; } = 20;
    /// <summary>The morning briefing starts by itself after the first request of the day between these hours.</summary>
    public bool MorningBriefing { get; set; } = true;
    public int BriefingFrom { get; set; } = 5;
    public int BriefingUntil { get; set; } = 12;
    /// <summary>The day of the last briefing (yyyy-MM-dd): one a day.</summary>
    public string LastBriefingDay { get; set; } = "";
    /// <summary>Phase 5: each area's source (missing = local). See <see cref="AreaSource"/>.</summary>
    public List<AreaSource> AreaSources { get; set; } = [];
    /// <summary>The account whose mail AL-76 reads and sends: "", "microsoft" or "google".</summary>
    public string MailAccount { get; set; } = "";
    /// <summary>The app registrations the user made (guide in the Accounts page): Microsoft needs only the ID,
    /// Google a "desktop app" ID and its secret. Empty = that account cannot be connected yet.</summary>
    public string MicrosoftClientId { get; set; } = "";
    public string GoogleClientId { get; set; } = "";
    public string GoogleClientSecret { get; set; } = "";
    /// <summary>Phase 6: a calendar link (ICS) with the lessons or the shifts, read every 6 hours; empty = none.</summary>
    public string TimetableUrl { get; set; } = "";
    /// <summary>At 21 a short summary of the day's study (only on days with study or an exam within two weeks).</summary>
    public bool EveningSummary { get; set; } = true;
    public int EveningHour { get; set; } = 21;
    public string LastEveningDay { get; set; } = "";
    /// <summary>The "university" module: how AL-76 names the university, the student app's server address and its User-Agent.</summary>
    public string UniversityName { get; set; } = "";
    public string UniversityServer { get; set; } = "";
    public string UniversityAgent { get; set; } = "";
    /// <summary>The "maker" module (phase 7). Folders with the Arduino sketches; empty = Documents\Arduino, Documents and the user's Projects.</summary>
    public List<string> MakerFolders { get; set; } = [];
    /// <summary>arduino-cli.exe when it is not where Arduino IDE 2 puts it; empty = look for it.</summary>
    public string ArduinoCli { get; set; } = "";
    /// <summary>Bambu Lab printer in LAN mode: address and serial number (the access code is kept encrypted).</summary>
    public string PrinterHost { get; set; } = "";
    public string PrinterSerial { get; set; } = "";
    /// <summary>rosbridge's WebSocket; empty = ws://localhost:9090 (ROS 2 in WSL2).</summary>
    public string RosBridgeUrl { get; set; } = "";
    /// <summary>The ROS 2 laboratory's folder (with pixi.toml) for lab_start; empty = not used.</summary>
    public string LabProject { get; set; } = "";
    /// <summary>A Samsung TV on the local network: its name or address (e.g. Samsung.local); empty = no TV.</summary>
    public string TvHost { get; set; } = "";
    /// <summary>Robot Console's local API, e.g. http://127.0.0.1:8765 (the session token is kept encrypted); empty = not used.</summary>
    public string RobotConsoleUrl { get; set; } = "";
    /// <summary>"claude", "chatgpt", "gemini" or "local"; empty = the first one ready, in that order.</summary>
    public string Brain { get; set; } = "";
    /// <summary>"Senza IA" (Mac 1.30.84): no model, local or online; only the commands AL-76 knows.</summary>
    public bool NoAI { get; set; }
    /// <summary>Model for the chosen cloud brain; empty = the CLI's default for the subscription.</summary>
    public string BrainModel { get; set; } = "";
    /// <summary>Ollama model for the local brain; empty = the one of the PC tier (none on Base).</summary>
    public string LocalModel { get; set; } = "";
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    /// <summary>Folders of the notes index, one per line in the settings; empty = Documents\Appunti and Documents\Notes.</summary>
    public List<string> NotesFolders { get; set; } = [];
    /// <summary>Ids of the optional modules switched on (all off by default).</summary>
    public List<string> EnabledModules { get; set; } = [];
    /// <summary>false = few confirmations (purchases, passwords, email); true = "Prudente".</summary>
    public bool CarefulConfirmations { get; set; }
    /// <summary>AL-76 may read and use other apps' windows and look at the screen (phase 4; Mac: appControlEnabled).</summary>
    public bool AppControl { get; set; } = true;
    public bool OnboardingDone { get; set; }
    /// <summary>AL-76 starts with Windows (Run key of the user, no admin).</summary>
    public bool StartWithWindows { get; set; } = true;
    /// <summary>"" = automatic (Kokoro on Medio/Alto, the Windows voice on Base), "windows" = the Windows voice,
    /// "kokoro" = Kokoro on any PC.</summary>
    public string VoiceEngine { get; set; } = "";
    /// <summary>Kokoro voice for each language, also a blend ("im_nicola:0.7+bm_george:0.3"). Chosen from the samples.</summary>
    public string KokoroVoiceIt { get; set; } = "im_nicola";
    public string KokoroVoiceEn { get; set; } = "af_heart";
    /// <summary>Look of the NUCLEUS, the small window that stays when the sphere is reduced: "reattore",
    /// "satellite" or "robottino" (3D robot from a model the user puts in the Companion folder; without it, the satellite).</summary>
    public string NucleusStyle { get; set; } = "reattore";
    /// <summary>Dark shaded disc behind the NUCLEUS (off: it blends completely with the wallpaper).</summary>
    public bool NucleusBackdrop { get; set; } = true;
    /// <summary>ROBOTTINO: the larger size (the smaller one, a fifth less, is the default).</summary>
    public bool RobotLarge { get; set; }
    /// <summary>ROBOTTINO: colour of the jet flames, "fuoco" or "ioniche".</summary>
    public string RobotFlames { get; set; } = "fuoco";
    /// <summary>ROBOTTINO: while AL-76 waits it lands on the bottom edge of the screen and walks there, engines off.</summary>
    public bool RobotWalks { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load(string file)
    {
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file), Json) ?? new AppConfig();
        }
        catch (JsonException) { /* a broken file falls back to the defaults; the old one stays on disk */ }
        return new AppConfig();
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>The language really in use: the chosen one, or the system one.</summary>
    public string EffectiveLanguage(string systemLanguage) =>
        Language is "it" or "en" ? Language : systemLanguage.StartsWith("it", StringComparison.OrdinalIgnoreCase) ? "it" : "en";

    public const string DefaultWakePhrase = "Ehi Al";

    /// <summary>The call in use: the default "Ehi Al" becomes "Hey Al" in English, so Whisper's hint is English too.
    /// A call the user chose stays as it is.</summary>
    public string EffectiveWakePhrase(string language) =>
        WakePhrase == DefaultWakePhrase && language == "en" ? "Hey Al" : WakePhrase;
}
