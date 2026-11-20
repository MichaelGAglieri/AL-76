using System.Text.Json;
using AL76.Core.Brains;
using AL76.Core.Config;
using AL76.Core.Localization;
using AL76.Core.Modules;

namespace AL76.Core.Setup;

/// <summary>The steps of the first-run setup, in the plan's order. Every step can be skipped.</summary>
public enum SetupStep { Welcome, You, StudyWork, Areas, Brain, Permissions, Voice, Modules, Done }

/// <summary>
/// The first-run setup for anyone (Mac: Onboarding, made generic): language, who you are, study and/or work,
/// areas, brain, permissions, voice and call, optional modules. The windows edit a copy of the settings; each
/// step is saved when you go on, so closing halfway keeps what was already written (as on the Mac).
/// </summary>
public static class SetupFlow
{
    public static readonly IReadOnlyList<SetupStep> Steps = Enum.GetValues<SetupStep>();

    public static bool AsksStudy(UserProfile p) => p is UserProfile.Studente or UserProfile.StudenteLavoratore;
    public static bool AsksWork(UserProfile p) => p is UserProfile.Lavoratore or UserProfile.StudenteLavoratore;

    /// <summary>The areas that fit a profile: a worker has no "Studio", a student no "Lavoro".</summary>
    public static List<string> DefaultAreas(UserProfile p, string language)
    {
        bool it = language != "en";
        string study = it ? "Studio" : "Study", work = it ? "Lavoro" : "Work", personal = it ? "Personale" : "Personal";
        return p switch
        {
            UserProfile.Studente => [study, personal],
            UserProfile.Lavoratore => [work, personal],
            UserProfile.StudenteLavoratore => [study, work, personal],
            _ => [personal],
        };
    }

    /// <summary>True when the areas are still some profile's defaults, in either language: the user did not
    /// write their own, so a new profile or language may replace them.</summary>
    public static bool AreDefault(IEnumerable<string> areas)
    {
        var list = areas.ToList();
        foreach (var p in Enum.GetValues<UserProfile>())
            foreach (var lang in new[] { "it", "en" })
                if (DefaultAreas(p, lang).SequenceEqual(list)) return true;
        // The defaults before the setup existed.
        return list.SequenceEqual(["Studio", "Lavoro", "Personale"]);
    }

    /// <summary>A new profile (or language) brings its areas, unless the user wrote their own.</summary>
    public static void ProfileChanged(AppConfig draft, string language)
    {
        if (!AreDefault(draft.Areas)) return;
        draft.Areas = DefaultAreas(draft.Profile, language);
        if (!draft.Areas.Contains(draft.ActiveArea)) draft.ActiveArea = draft.Areas[0];
    }

    private static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>The copy the windows edit.</summary>
    public static AppConfig Copy(AppConfig c) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(c, Json), Json) ?? new AppConfig();

    /// <summary>
    /// Copies the edited settings onto the ones in use, tidied: trimmed texts, areas without empties or doubles
    /// (at least one), the active area among them, a known brain, a call and a name never empty.
    /// The running assistant shares the live object, so it sees the change at once.
    /// </summary>
    public static void Apply(AppConfig draft, AppConfig live, bool finished = false)
    {
        Normalize(draft);
        // What AL-76 itself keeps up to date while a window is open is not taken back from the copy.
        foreach (var p in typeof(AppConfig).GetProperties().Where(p => p.CanRead && p.CanWrite && p.Name is not (nameof(AppConfig.LastBriefingDay) or nameof(AppConfig.LastEveningDay))))
            p.SetValue(live, p.GetValue(draft) is List<string> list ? new List<string>(list) : p.GetValue(draft));
        if (finished) live.OnboardingDone = true;
    }

    public static void Normalize(AppConfig c)
    {
        c.UserName = c.UserName.Trim();
        c.Honorific = c.Honorific.Trim();
        c.About = c.About.Trim();
        c.Study = c.Study.Trim();
        c.Work = c.Work.Trim();
        c.Interests = c.Interests.Trim();
        c.City = c.City.Trim();
        c.AssistantName = c.AssistantName.Trim().Length > 0 ? c.AssistantName.Trim() : "AL-76";
        c.WakePhrase = c.WakePhrase.Trim().Trim('.', '!', ',').Length > 0 ? c.WakePhrase.Trim().Trim('.', '!', ',') : AppConfig.DefaultWakePhrase;
        var areas = new List<string>();
        foreach (var a in c.Areas.Select(a => a.Trim()).Where(a => a.Length > 0))
            if (!areas.Contains(a, StringComparer.OrdinalIgnoreCase)) areas.Add(a);
        if (areas.Count == 0) areas.Add(c.EffectiveLanguage("it") == "en" ? "Personal" : "Personale");
        c.Areas = areas;
        c.ActiveArea = areas.FirstOrDefault(a => a.Equals(c.ActiveArea.Trim(), StringComparison.OrdinalIgnoreCase)) ?? areas[0];
        if (c.Brain is not ("" or "claude" or "chatgpt" or "gemini" or "local")) c.Brain = "";
        if (c.NucleusStyle is not ("reattore" or "satellite" or "robottino")) c.NucleusStyle = "reattore";
        if (c.RobotFlames is not ("fuoco" or "ioniche")) c.RobotFlames = "fuoco";
        c.NotesFolders = c.NotesFolders.Select(f => f.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        c.EnabledModules = c.EnabledModules.Where(id => ModuleRegistry.All.Any(m => m.Id == id)).Distinct().ToList();
        c.AreaSources = c.AreaSources.Where(s => s.Provider is "microsoft" or "google" && areas.Contains(s.Area, StringComparer.OrdinalIgnoreCase))
                                     .GroupBy(s => s.Area, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        foreach (var s in c.AreaSources) { s.Calendar = s.Calendar.Trim(); s.TaskList = s.TaskList.Trim(); }
        if (c.MailAccount is not ("" or "microsoft" or "google")) c.MailAccount = "";
        c.MicrosoftClientId = c.MicrosoftClientId.Trim();
        c.GoogleClientId = c.GoogleClientId.Trim();
        c.GoogleClientSecret = c.GoogleClientSecret.Trim();
    }

    /// <summary>A study profile with nothing written about the study is incomplete, and so is a work one.</summary>
    public static bool StudyWorkComplete(AppConfig c) =>
        (!AsksStudy(c.Profile) || c.Study.Trim().Length > 0) && (!AsksWork(c.Profile) || c.Work.Trim().Length > 0);

    public sealed record Line(bool Ok, string Text);

    /// <summary>What the "Pronto" page lists: what AL-76 learnt, each with whether it is in place.</summary>
    public static List<Line> Summary(AppConfig c, Strings s, IReadOnlyDictionary<string, BrainStatus> brains, bool microphone)
    {
        var lines = new List<Line>
        {
            c.UserName.Length > 0 ? new(true, s.Format("setup.sum.name", c.UserName)) : new(false, s["setup.sum.noname"]),
            new(true, s.Format("setup.sum.profile", s["profile." + c.Profile])),
        };
        if (AsksStudy(c.Profile)) lines.Add(c.Study.Length > 0 ? new(true, s.Format("setup.sum.study", c.Study)) : new(false, s["setup.sum.nostudy"]));
        if (AsksWork(c.Profile)) lines.Add(c.Work.Length > 0 ? new(true, s.Format("setup.sum.work", c.Work)) : new(false, s["setup.sum.nowork"]));
        lines.Add(new(true, s.Format("setup.sum.areas", string.Join(", ", c.Areas))));
        if (c.Brain.Length == 0)
            lines.Add(new(brains.Values.Any(b => b == BrainStatus.Ready), s["setup.sum.brain.auto"]));
        else
        {
            bool ready = brains.TryGetValue(c.Brain, out var st) && st == BrainStatus.Ready;
            lines.Add(new(ready, s.Format(ready ? "setup.sum.brain" : "setup.sum.brain.notready", BrainTitle(c.Brain, s))));
        }
        lines.Add(new(microphone, s[microphone ? "setup.sum.mic" : "setup.sum.nomic"]));
        lines.Add(new(true, s.Format("setup.sum.call", c.EffectiveWakePhrase(s.Language))));
        int modules = c.EnabledModules.Count;
        lines.Add(new(true, modules == 0 ? s["setup.sum.nomodules"] : s.Format("setup.sum.modules", string.Join(", ", c.EnabledModules.Select(id => s["module." + id])))));
        return lines;
    }

    public static string BrainTitle(string id, Strings s) => id switch
    {
        "local" => s["brain.local"],
        "" => s["brain.auto"],
        _ => BrainCatalog.Find(id)?.Title ?? id,
    };
}
