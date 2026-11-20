using AL76.Core.Config;
using AL76.Core.Personal;
using AL76.Core.Safety;
using AL76.Core.Web;
using static AL76.Core.Tools.ToolDef;

namespace AL76.Core.Tools;

/// <summary>
/// Reminders (local, by area), weather, web search and page reading, battery, folders, dark mode, screen off:
/// the tools of the Mac whose Windows backend needs no account. Sites read from a search are trusted for the
/// rest of the request; any other page needs the "yes" (a page could carry data out in its address).
/// </summary>
public static class MoreTools
{
    public static IEnumerable<ToolDef> Create(AppConfig config, IPcActions pc, HashSet<string> searchedHosts)
    {
        static string L(ToolContext c, string it, string en) => c.Italian ? it : en;
        static ToolOutcome Pc(PcResult r) => new ToolOutcome.Done(r.ForModel, !r.Ok);

        yield return new ToolDef
        {
            Name = "get_weather",
            Description = "Current weather and the next three days. Empty location = where the user is (from the internet connection or the settings).",
            Properties = new() { ["location"] = Prop("string", "City; empty for the user's own.") },
            Run = async (a, c, ct) =>
            {
                try { return ToolOutcome.Ok(await WebActions.WeatherAsync(a.Str("location") is { Length: > 0 } p ? p : config.City, c.Italian, ct)); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                { return ToolOutcome.Error(L(c, "Il meteo non risponde.", "The weather service is not answering.") + " " + e.Message); }
            },
        };
        yield return new ToolDef
        {
            Name = "web_search",
            Description = "Search the web (current facts, news, addresses of sites). Returns titles, snippets and links.",
            Properties = new() { ["query"] = Prop("string", "What to search.") },
            Required = ["query"],
            Run = async (a, c, ct) =>
            {
                try
                {
                    var results = await WebActions.SearchAsync(a.Str("query"), ct);
                    lock (searchedHosts)
                        foreach (var r in results)
                            if (Uri.TryCreate(r.Url, UriKind.Absolute, out var u)) searchedHosts.Add(u.Host.StartsWith("www.") ? u.Host[4..] : u.Host);
                    return ToolOutcome.Ok(WebActions.FormatResults(a.Str("query"), results));
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return ToolOutcome.Error("Search failed: " + e.Message); }
            },
        };
        yield return new ToolDef
        {
            Name = "read_webpage",
            Description = "Read the text of a web page found with web_search (or named by the user).",
            Properties = new() { ["url"] = Prop("string", "The page address.") },
            Required = ["url"],
            Run = async (a, c, ct) =>
            {
                string[] trusted;
                lock (searchedHosts) trusted = searchedHosts.ToArray();
                var (verdict, url) = Security.CheckUrl(a.Str("url"), c.Command, trusted);
                async Task<string> Read(CancellationToken t)
                {
                    try { return Security.External(url!.Host, await WebActions.ReadPageAsync(url!, t)); }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return "Page not reachable: " + e.Message; }
                }
                return verdict switch
                {
                    Security.UrlVerdict.Refused => ToolOutcome.Error("Only http and https addresses."),
                    Security.UrlVerdict.NeedsYes => new ToolOutcome.NeedsYes(new PendingAction(
                        L(c, $"Leggere la pagina {url!.Host}, che non viene da una tua ricerca", $"Read the page {url!.Host}, which did not come from your search"), Risk.UnknownSite, Read)),
                    _ => ToolOutcome.Ok(await Read(ct)),
                };
            },
        };
        yield return new ToolDef
        {
            Name = "system_status",
            Description = "Battery level and charging state of the PC.",
            Run = async (a, c, ct) => Pc(await pc.BatteryAsync(ct)),
        };
        yield return new ToolDef
        {
            Name = "open_folder",
            Description = "Open a folder in File Explorer: desktop, documents, downloads, pictures, music, videos, or a folder by name.",
            Properties = new() { ["name"] = Prop("string", "Folder name or known place.") },
            Required = ["name"],
            Run = async (a, c, ct) => Pc(await pc.OpenFolderAsync(a.Str("name"), ct)),
        };
        yield return new ToolDef
        {
            Name = "create_folder",
            Description = "Create a new folder (never replaces an existing one).",
            Properties = new() { ["name"] = Prop("string", "Folder name."), ["place"] = Prop("string", "Where.", "desktop", "documents", "downloads", "pictures", "music", "videos") },
            Required = ["name"],
            Run = async (a, c, ct) => Pc(await pc.CreateFolderAsync(a.Str("name"), a.Str("place") is { Length: > 0 } p ? p : null, ct)),
        };
        yield return new ToolDef
        {
            Name = "set_dark_mode",
            Description = "Switch Windows between dark and light mode.",
            Properties = new() { ["on"] = Prop("boolean", "true for dark, false for light.") },
            Required = ["on"],
            Run = async (a, c, ct) => Pc(await pc.SetDarkModeAsync(a.Bool("on") ?? true, ct)),
        };
        yield return new ToolDef
        {
            Name = "sleep_display",
            Description = "Turn the screen off (it comes back with the mouse or a key).",
            Run = async (a, c, ct) => Pc(await pc.SleepDisplayAsync(ct)),
        };
    }

    /// <summary>Tool results that are spoken as they are after a direct command.</summary>
    public static class Spoken
    {
        public static string When(DateTime due, bool italian, DateTime now)
        {
            var culture = new System.Globalization.CultureInfo(italian ? "it-IT" : "en-GB");
            string day = due.Date == now.Date ? (italian ? "oggi" : "today")
                : due.Date == now.Date.AddDays(1) ? (italian ? "domani" : "tomorrow")
                : due.Date < now.Date.AddDays(7) ? due.ToString("dddd", culture)
                : due.ToString(italian ? "d MMMM" : "MMMM d", culture);
            if (due.TimeOfDay == TimeSpan.Zero && due.Hour == 0) return day;
            string time = italian
                ? (due.Hour == 1 ? "all'una" : $"alle {due.Hour}") + (due.Minute > 0 ? $" e {due.Minute}" : "")
                : $"at {due.ToString(due.Minute > 0 ? "h:mm tt" : "h tt", System.Globalization.CultureInfo.GetCultureInfo("en-US"))}";
            return $"{day} {time}";
        }

        public static string NewReminder(string title, DateTime? due, bool italian, DateTime? now = null) =>
            due is DateTime d
                ? (italian ? $"Promemoria creato: {title}, {When(d, true, now ?? DateTime.Now)}." : $"Reminder set: {title}, {When(d, false, now ?? DateTime.Now)}.")
                : (italian ? $"Promemoria creato: {title}." : $"Reminder set: {title}.");

        public static string Reminders(List<(string Title, DateTime? Due)> list, bool italian, DateTime now)
        {
            if (list.Count == 0) return italian ? "Non hai promemoria in sospeso." : "You have no open reminders.";
            var items = list.Take(6).Select(r => r.Due is DateTime d ? $"{r.Title}, {When(d, italian, now)}" : r.Title).ToList();
            string head = italian ? (list.Count == 1 ? "Hai un promemoria: " : $"Hai {list.Count} promemoria: ")
                                  : (list.Count == 1 ? "You have one reminder: " : $"You have {list.Count} reminders: ");
            string more = list.Count > 6 ? (italian ? $"; e altri {list.Count - 6}." : $"; and {list.Count - 6} more.") : ".";
            return head + string.Join("; ", items) + more;
        }
    }
}
