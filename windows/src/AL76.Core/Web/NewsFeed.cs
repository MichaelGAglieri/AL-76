using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace AL76.Core.Web;

/// <summary>
/// The day's news (Mac: NewsFeed, 12 November): the public RSS and Atom feeds of a few newsrooms, no account and no
/// key. Only headlines and the feeds' own short summaries are read; an article is opened only when asked
/// (news_detail). What was given is remembered 4 days in notizie_date.json, so the same story is never told
/// twice, not even from another site (Mac, 16 November). The sources and the topics are the user's choice
/// (Impostazioni); the default follows technology, AI and robotics, in Italian and English.
/// </summary>
public sealed class NewsFeed(string givenFile, Func<IReadOnlyList<NewsSource>> sources, Func<IReadOnlyList<string>> topics,
                             Func<NewsSource, CancellationToken, Task<string?>>? download = null)
{
    public sealed record Item(string Source, string Title, string Summary, string Link, DateTime Date)
    {
        public double Score { get; set; }
        /// <summary>Other sites with the same story: a sign it is confirmed.</summary>
        public List<string> AlsoOn { get; init; } = [];
    }

    private sealed record Given(DateTime At, Item Item);

    public static readonly NewsSource[] DefaultSources =
    [
        new("IEEE Spectrum", "https://spectrum.ieee.org/feeds/topic/robotics.rss", 2),
        new("The Robot Report", "https://www.therobotreport.com/feed/", 2),
        new("Sky TG24", "https://tg24.sky.it/rss/tg24_tecnologia.xml"),
        new("ANSA", "https://www.ansa.it/canale_tecnologia/notizie/tecnologia_rss.xml"),
        new("la Repubblica", "https://www.repubblica.it/rss/tecnologia/rss2.0.xml"),
        new("Il Sole 24 Ore", "https://www.ilsole24ore.com/rss/tecnologia.xml"),
        new("DDay", "https://www.dday.it/rss"),
        new("HDblog", "https://www.hdblog.it/feed/"),
        new("Bloomberg", "https://feeds.bloomberg.com/technology/news.rss"),
        new("The Verge", "https://www.theverge.com/rss/index.xml"),
        new("Ars Technica", "https://feeds.arstechnica.com/arstechnica/index"),
        new("MIT Technology Review", "https://www.technologyreview.com/feed/"),
        new("TechCrunch", "https://techcrunch.com/category/artificial-intelligence/feed/"),
        new("Wired", "https://www.wired.com/feed/tag/ai/latest/rss"),
        new("Sky News", "https://feeds.skynews.com/feeds/rss/technology.xml"),
    ];

    /// <summary>Read only for "dimmi altre notizie" and for a topic: the large newspapers and the main device sites.</summary>
    public static readonly NewsSource[] MoreSources =
    [
        new("BBC", "https://feeds.bbci.co.uk/news/technology/rss.xml"),
        new("The Guardian", "https://www.theguardian.com/technology/rss"),
        new("New York Times", "https://rss.nytimes.com/services/xml/rss/nyt/Technology.xml"),
        new("Financial Times", "https://www.ft.com/technology?format=rss"),
        new("CNBC", "https://www.cnbc.com/id/19854910/device/rss/rss.html"),
        new("Robohub", "https://robohub.org/feed/", 2),
        new("MIT News", "https://news.mit.edu/topic/mitrobotics-rss.xml", 2),
        new("Engadget", "https://www.engadget.com/rss.xml"),
        new("GSMArena", "https://www.gsmarena.com/rss-news-reviews.php3"),
        new("Android Authority", "https://www.androidauthority.com/feed/"),
        new("Tom's Hardware", "https://www.tomshardware.com/feeds/all"),
        new("Windows Central", "https://www.windowscentral.com/feeds/all"),
        new("CNET", "https://www.cnet.com/rss/news/"),
    ];

    /// <summary>What is worth telling, with a weight (as on the Mac): robotics, AI, making, devices, space.</summary>
    private static readonly (Regex pattern, double weight)[] Interests =
    [
        (new(@"\b(robot\w*|umanoid\w*|humanoid\w*|cobot\w*|esoschelet\w*|exoskeleton\w*|ros ?2|boston dynamics|unitree|optimus|figure ai)\b"), 4),
        (new(@"\b(dron[ei]|drones?|automazione|automation|guida autonoma|self-driving|autonomous|robotaxi|waymo)\b"), 2.5),
        (new(@"\b(ai|ia|intelligenza artificiale|artificial intelligence|openai|anthropic|claude|chatgpt|gpt-?\d\w*|gemini|deepmind|llm|agenti? ai|ai agents?|nvidia|machine learning|mistral|deepseek|copilot)\b"), 3),
        (new(@"\b(stampa(nte|nti)? 3d|3d print\w*|bambu\w*|arduino|raspberry pi|maker|open source|open-source|esp32)\b"), 3),
        (new(@"\b(iphone|ipad|macbook|mac|macos|ios|apple|samsung|galaxy|pixel|android|xiaomi|windows|microsoft|surface|visore|headset|chip|processor[ei]?|semiconduttor\w*|semiconductor\w*|tsmc|batteri[ae]|smartphone|wearable)\b"), 1.5),
        (new(@"\b(spacex|starship|nasa|esa|satellit\w*|quantum|quantistic\w*|fusione nucleare|fusion)\b"), 1.5),
    ];

    /// <summary>Shopping pages and the like: not news.</summary>
    private static readonly Regex Noise = new(@"\b(offert[ae]|sconto|sconti|black friday|coupon|deals?|discounts?|sale|miglior[ei] \w+ da comprare|best \w+ deals|oroscopo|quanto costa|prezzo più basso|in promo)\b");

    private static readonly TimeSpan Memory = TimeSpan.FromDays(4);
    private static readonly HttpClient Http = CreateClient();
    private readonly object gate = new();
    private List<Given>? given;
    private (DateTime at, bool wide, List<Item> items)? cache;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        return c;
    }

    private List<Given> GivenList()
    {
        if (given is not null) return given;
        try { given = JsonSerializer.Deserialize<List<Given>>(File.ReadAllText(givenFile)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { given = []; }
        return given;
    }

    /// <summary>The news given in the last 18 hours ("fai un report delle notizie che mi hai detto").</summary>
    public List<Item> AlreadyGiven(DateTime now)
    {
        lock (gate)
        {
            GivenList().RemoveAll(g => now - g.At > Memory);
            return given!.Where(g => now - g.At <= TimeSpan.FromHours(18)).Select(g => g.Item).ToList();
        }
    }

    /// <summary>Told in the last days: the same link, the same story from another site, or the same affair in the last two days.</summary>
    private bool ToldBefore(Item item, DateTime now)
    {
        var list = GivenList();
        if (list.Any(g => g.Item.Link == item.Link || Similar(g.Item.Title, item.Title))) return true;
        return list.Where(g => now - g.At < TimeSpan.FromDays(2)).Any(g => Related(g.Item.Title, item.Title));
    }

    private void Remember(IEnumerable<Item> items, DateTime now)
    {
        var list = GivenList();
        list.RemoveAll(g => now - g.At > Memory);
        foreach (var item in items)
            if (!list.Any(g => g.Item.Link == item.Link)) list.Add(new Given(now, item));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(givenFile)!);
            File.WriteAllText(givenFile, JsonSerializer.Serialize(list));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Never those already given. <paramref name="more"/>: the large newspapers too and two days back. If
    /// nothing new is left in the usual sites, it looks in the others by itself.</summary>
    public async Task<List<Item>> LatestAsync(DateTime now, int limit = 8, string? topic = null, bool more = false, CancellationToken ct = default)
    {
        double hours = more ? 48 : 36;
        List<Item> all;
        bool wide = more || topic is not null;
        var cached = cache;
        if (cached is { } c && now - c.at < TimeSpan.FromMinutes(10) && (c.wide || !wide)) all = c.items.ToList();
        else
        {
            var list = sources().ToList();
            if (wide) list.AddRange(MoreSources.Where(m => !list.Any(s => s.Url == m.Url)));
            var fetched = await Task.WhenAll(list.Select(s => FetchAsync(s, ct)));
            all = fetched.SelectMany(x => x).ToList();
            if (all.Count > 0) cache = (now, wide, all.ToList());
        }
        List<Item> chosen;
        lock (gate)
        {
            GivenList().RemoveAll(g => now - g.At > Memory);
            var fresh = all.Where(i => !ToldBefore(i, now)).Select(i => i with { AlsoOn = [] }).ToList();
            foreach (var i in fresh) i.Score = all.First(x => x.Link == i.Link).Score;
            chosen = Rank(fresh, now, hours, limit, topic, topics());
        }
        if (chosen.Count < Math.Min(3, limit) && !more) return await LatestAsync(now, limit, topic, true, ct);
        lock (gate) Remember(chosen, now);
        return chosen;
    }

    public static List<Item> Rank(List<Item> items, DateTime now, double hours, int limit, string? topic, IReadOnlyList<string> userTopics)
    {
        var since = now.AddHours(-hours);
        var wanted = topic is null ? [] : Fold(topic).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToArray();
        var own = userTopics.Select(Fold).Where(t => t.Length > 2).ToArray();
        var ranked = new List<Item>();
        foreach (var original in items.Where(i => i.Date >= since && i.Date <= now.AddHours(1)))
        {
            string text = Fold(original.Title + " " + original.Summary);
            if (Noise.IsMatch(text)) continue;
            var item = original with { AlsoOn = [] };
            item.Score = original.Score;
            if (wanted.Length > 0)
            {
                int hits = wanted.Count(w => Regex.IsMatch(text, @"\b" + Regex.Escape(w)));
                if (hits == 0) continue;
                item.Score += hits * 5;
            }
            // A match in the title counts double.
            string title = Fold(item.Title);
            foreach (var (pattern, weight) in Interests)
            {
                if (pattern.IsMatch(title)) item.Score += weight * 2;
                else if (pattern.IsMatch(text)) item.Score += weight;
            }
            // The user's own topics (Impostazioni › Notizie) weigh like robotics.
            foreach (var t in own)
            {
                if (Regex.IsMatch(title, @"\b" + Regex.Escape(t))) item.Score += 8;
                else if (Regex.IsMatch(text, @"\b" + Regex.Escape(t))) item.Score += 4;
            }
            if (item.Score <= 0) continue;
            item.Score += Math.Max(0, 1 - (now - item.Date).TotalHours / hours);   // fresher first, a little
            ranked.Add(item);
        }
        // A story carried by several newsrooms is the news of the day: it goes up.
        ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
        var snapshot = ranked.Select(r => (r.Source, r.Title)).ToList();
        foreach (var r in ranked.Take(80))
        {
            int others = snapshot.Where(s => s.Source != r.Source && Similar(s.Title, r.Title)).Select(s => s.Source).Distinct().Count();
            r.Score += Math.Min(others, 4) * 2;
        }
        ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
        // The same story once; at most two items per site; one big affair does not fill the list.
        var chosen = new List<Item>();
        var perSource = new Dictionary<string, int>();
        foreach (var item in ranked)
        {
            if (chosen.FirstOrDefault(c => Similar(c.Title, item.Title)) is { } twin)
            {
                if (twin.Source != item.Source && !twin.AlsoOn.Contains(item.Source)) twin.AlsoOn.Add(item.Source);
                continue;
            }
            if (perSource.GetValueOrDefault(item.Source) >= 2 || chosen.Any(c => Related(c.Title, item.Title))) continue;
            chosen.Add(item);
            perSource[item.Source] = perSource.GetValueOrDefault(item.Source) + 1;
        }
        return chosen.Take(limit).ToList();
    }

    /// <summary>For the model: source, time, title, the feed's summary, the other sites and the link.</summary>
    public static string Spoken(List<Item> items, string? topic, bool italian)
    {
        if (items.Count == 0)
            return topic is null
                ? (italian ? "Nessun'altra notizia nuova: i siti non rispondono, o quelle di oggi sono già state dette." : "No other new news: the sites do not answer, or today's were already told.")
                : (italian ? $"Nessuna notizia recente su {topic}." : $"No recent news about {topic}.");
        var culture = CultureInfo.GetCultureInfo(italian ? "it-IT" : "en-GB");
        return string.Join("\n", items.Select(i =>
        {
            string also = i.AlsoOn.Count == 0 ? "" : (italian ? " [confermata anche da " : " [also on ") + string.Join(", ", i.AlsoOn.Take(3)) + "]";
            return $"- [{i.Source}, {i.Date.ToString("ddd HH:mm", culture)}] {i.Title}{(i.Summary.Length == 0 ? "" : " — " + i.Summary)}{also} ({i.Link})";
        }));
    }

    /// <summary>The news already given that the user asks about, even with the name misheard ("Proxis 1" is "Praxis-1").</summary>
    public Item? GivenItem(string query, DateTime now)
    {
        var skip = new HashSet<string> { "progetto", "notizia", "notizie", "dettagli", "informazioni", "quella", "quello", "della", "dello", "sulla", "sul", "news", "about", "more",
            "articolo", "storia", "nuovo", "nuova", "nuovi", "capo", "cosa", "come", "the", "dimmi", "dammi", "piu", "con", "per", "che", "del", "dei", "una", "uno" };
        string[] Words(string s) => Regex.Split(Fold(s), @"[^\p{L}\p{N}]+").Where(w => w.Length > 2 && !skip.Contains(w)).ToArray();
        var wanted = Words(query);
        if (wanted.Length == 0) return null;
        (Item item, double score)? best = null;
        foreach (var item in AlreadyGiven(now))
        {
            var title = Words(item.Title);
            var summary = Words(item.Summary);
            double score = 0;
            foreach (var word in wanted)
            {
                // Whisper keeps the first letter and the length and gets the vowels wrong: "proxys" is "praxis".
                int limit = word.Length >= 5 ? 2 : word.Length >= 4 ? 1 : 0;
                bool Close(string w) => w == word || (w[0] == word[0] && Distance(w, word) <= limit)
                                        || (word.Length >= 6 && w.Length >= 5 && w[..2] == word[..2] && Distance(w, word) <= 3);
                if (title.Any(Close)) score += 2;
                else if (summary.Any(Close)) score += 1;
            }
            score /= wanted.Length;
            if (score > (best?.score ?? 0)) best = (item, score);
        }
        return best is { score: >= 0.6 } b ? b.item : null;
    }

    private async Task<List<Item>> FetchAsync(NewsSource source, CancellationToken ct)
    {
        try
        {
            string? xml = download is not null ? await download(source, ct) : await Http.GetStringAsync(source.Url, ct);
            return xml is null ? [] : Parse(xml, source);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or TaskCanceledException) { return []; }
    }

    /// <summary>RSS items and Atom entries: title, link (Atom href, or the guid as Sky TG24 does), date, summary.</summary>
    public static List<Item> Parse(string xml, NewsSource source)
    {
        var items = new List<Item>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            Dictionary<string, string>? fields = null;
            // The field being read and its depth: markup inside it (XHTML summaries) adds its text to it.
            string field = "";
            int fieldDepth = -1;
            string[] wanted = ["title", "description", "summary", "link", "guid", "pubDate", "published", "updated", "dc:date"];
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (field.Length > 0) break;
                        if (reader.LocalName is "item" or "entry") { fields = reader.IsEmptyElement ? null : []; break; }
                        if (fields is null) break;
                        if (reader.LocalName == "link" && reader.GetAttribute("href") is string href && (reader.GetAttribute("rel") ?? "alternate") == "alternate")
                            fields["link"] = href;
                        if (!reader.IsEmptyElement && wanted.Contains(reader.Name)) { field = reader.Name; fieldDepth = reader.Depth; }
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                        if (fields is not null && field.Length > 0) fields[field] = fields.GetValueOrDefault(field, "") + reader.Value;
                        break;
                    case XmlNodeType.EndElement:
                        if (field.Length > 0)
                        {
                            if (reader.Depth == fieldDepth) field = "";
                            break;
                        }
                        if (reader.LocalName is "item" or "entry" && fields is not null)
                        {
                            if (Build(fields, source) is { } item) items.Add(item);
                            fields = null;
                        }
                        break;
                }
            }
        }
        catch (XmlException) { }
        return items;
    }

    private static Item? Build(Dictionary<string, string> f, NewsSource source)
    {
        string title = Plain(f.GetValueOrDefault("title", ""), 200);
        string link = f.GetValueOrDefault("link", "").Trim();
        if (!link.StartsWith("http")) link = f.GetValueOrDefault("guid", "").Trim();
        string raw = f.GetValueOrDefault("pubDate") ?? f.GetValueOrDefault("published") ?? f.GetValueOrDefault("updated") ?? f.GetValueOrDefault("dc:date") ?? "";
        if (title.Length == 0 || !link.StartsWith("http") || ParseDate(raw) is not DateTime date) return null;
        string summary = Plain(f.GetValueOrDefault("description") ?? f.GetValueOrDefault("summary") ?? "", 220);
        return new Item(source.Name, title, summary == title ? "" : summary, link, date) { Score = source.Bonus };
    }

    /// <summary>RFC 822 ("Thu, 12 Nov 2026 16:33:17 +0000"), Sky's Italian variant ("gio, 12 nov 2026 12:45:00 GMT") and ISO 8601; local time.</summary>
    public static DateTime? ParseDate(string raw)
    {
        string text = raw.Trim();
        if (text.Length == 0) return null;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var iso) && text.Contains('T'))
            return iso.LocalDateTime;
        int comma = text.IndexOf(", ", StringComparison.Ordinal);
        string body = comma >= 0 ? text[(comma + 2)..] : text;
        body = Regex.Replace(body, @"\s(GMT|UT|UTC|Z)$", " +0000");
        body = Regex.Replace(body, @"\s(EST|EDT|CST|CDT|PST|PDT)$", m => m.Value.Trim() switch
        {
            "EST" => " -0500", "EDT" => " -0400", "CST" => " -0600", "CDT" => " -0500", "PST" => " -0800", _ => " -0700",
        });
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("it-IT") })
            foreach (var format in new[] { "d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm zzz", "d MMM yyyy HH:mm:ss" })
            {
                string candidate = Regex.Replace(body, @"([+-]\d{2})(\d{2})$", "$1:$2");
                if (DateTimeOffset.TryParseExact(candidate, format, culture, DateTimeStyles.AssumeUniversal, out var d)) return d.LocalDateTime;
            }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var any) ? any.LocalDateTime : null;
    }

    public static string Plain(string html, int limit)
    {
        string text = Regex.Replace(html, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(System.Net.WebUtility.HtmlDecode(text));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length <= limit) return text;
        string cut = text[..limit];
        int space = cut.LastIndexOf(' ');
        return (space > 0 ? cut[..space] : cut) + "…";
    }

    public static string Fold(string text)
    {
        var sb = new StringBuilder();
        foreach (char ch in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString();
    }

    private static HashSet<string> LongWords(string s, int min) =>
        Regex.Split(Fold(s), @"[^\p{L}\p{N}]+").Where(w => w.Length > min).ToHashSet();

    public static bool Similar(string a, string b)
    {
        var x = LongWords(a, 3);
        var y = LongWords(b, 3);
        if (x.Count == 0 || y.Count == 0) return false;
        return (double)x.Intersect(y).Count() / Math.Min(x.Count, y.Count) >= 0.6;
    }

    /// <summary>Two titles on the same affair: at least two long words in common ("jensen huang", "nvidia").</summary>
    public static bool Related(string a, string b) => LongWords(a, 4).Intersect(LongWords(b, 4)).Count() >= 2;

    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 3) return 9;
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int previous = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int current = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }
        }
        return row[b.Length];
    }
}

/// <summary>A news feed: the name said aloud, the RSS or Atom address, and a head start for feeds all on a topic.</summary>
public sealed record NewsSource(string Name, string Url, double Bonus = 0)
{
    /// <summary>"Nome | https://…" as written in the settings.</summary>
    public static NewsSource? FromLine(string line)
    {
        var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
        string url = parts.Length == 2 ? parts[1] : parts[0];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return null;
        string name = parts.Length == 2 && parts[0].Length > 0 ? parts[0] : u.Host.Replace("www.", "");
        return new NewsSource(name, u.AbsoluteUri);
    }
}
