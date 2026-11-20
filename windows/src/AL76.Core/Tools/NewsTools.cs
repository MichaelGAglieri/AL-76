using AL76.Core.Safety;
using AL76.Core.Web;
using static AL76.Core.Tools.ToolDef;

namespace AL76.Core.Tools;

/// <summary>news and news_detail (Mac, 12 and 16 November). The sites of the news given count as asked for: their
/// articles can then be read without the "yes" for unknown sites.</summary>
public static class NewsTools
{
    public static IEnumerable<ToolDef> Create(NewsFeed feed, HashSet<string> trustedHosts)
    {
        static string L(ToolContext c, string it, string en) => c.Italian ? it : en;

        void Trust(IEnumerable<NewsFeed.Item> items)
        {
            lock (trustedHosts)
                foreach (var i in items)
                    if (Uri.TryCreate(i.Link, UriKind.Absolute, out var u)) trustedHosts.Add(u.Host.StartsWith("www.") ? u.Host[4..] : u.Host);
        }

        yield return new ToolDef
        {
            Name = "news",
            Description = "Today's and yesterday's news (technology, AI, robotics, devices, and the user's own topics) from news sites such as ANSA, Sky TG24, "
                          + "Bloomberg, The Verge, IEEE Spectrum. For \"che notizie ci sono?\", \"novità sull'intelligenza artificiale?\". Say 3 or 4, one sentence each in the "
                          + "user's language, naming the source. Report only what the items say: never add facts.",
            Properties = new()
            {
                ["topic"] = Prop("string", "Optional: only news about this, a few keywords in Italian and English (e.g. robot umanoidi humanoid, Windows Microsoft)."),
                ["recap"] = Prop("boolean", "true: ALL the news already given today, nothing new (\"le notizie che mi hai detto\", \"fai un report delle notizie\")."),
                ["more"] = Prop("boolean", "true for \"continua\", \"dimmi altre notizie\": the next ones, never those already said, also from the large newspapers."),
            },
            Run = async (a, c, ct) =>
            {
                var now = DateTime.Now;
                if (a.Bool("recap") == true)
                {
                    var given = feed.AlreadyGiven(now);
                    return given.Count == 0 ? ToolOutcome.Ok(L(c, "Oggi non ho ancora dato notizie.", "No news given today yet."))
                        : ToolOutcome.Ok(Security.External(L(c, "notizie già date oggi", "news already given today"), NewsFeed.Spoken(given, null, c.Italian)));
                }
                string? topic = a.Str("topic") is { Length: > 0 } t ? t : null;
                var items = await feed.LatestAsync(now, 8, topic, a.Bool("more") == true, ct);
                Trust(items);
                return ToolOutcome.Ok(Security.External(L(c, "notizie", "news"), NewsFeed.Spoken(items, topic, c.Italian)));
            },
        };
        yield return new ToolDef
        {
            Name = "news_detail",
            Description = "More about a news item already given today (\"dammi più dettagli su…\", \"approfondisci quella su…\"): finds it even if the name was "
                          + "misheard and reads the article. Try this BEFORE web_search when the user asks about a name that may come from the news.",
            Properties = new() { ["about"] = Prop("string", "What the user asked about, as heard (e.g. Proxis 1, il robot di Amazon).") },
            Required = ["about"],
            Run = async (a, c, ct) =>
            {
                if (feed.GivenItem(a.Str("about"), DateTime.Now) is not { } item)
                    return ToolOutcome.Ok($"None of today's news matches \"{a.Str("about")}\". Use web_search.");
                Trust([item]);
                // The article and, meanwhile, what other sites say, searched with the right name.
                var page = Uri.TryCreate(item.Link, UriKind.Absolute, out var url) ? Safe(() => WebActions.ReadPageAsync(url, ct)) : Task.FromResult("");
                var others = Safe(async () => string.Join("\n", (await WebActions.SearchAsync(item.Title, ct)).Take(5).Select(r => $"- {r.Title} — {r.Snippet} [{r.Url}]")));
                string article = await page, more = await others;
                string text = $"[{item.Source}] {item.Title}\n{item.Summary}\nLink: {item.Link}\n\nARTICLE:\n{Cut(article, 4000)}\n\nOTHER SOURCES (web search on the title):\n{Cut(more, 1500)}";
                return ToolOutcome.Ok(Security.External(L(c, "notizia", "news item"), text)
                    + "\nAnswer what the user asked from the article, in a few sentences; add what the other sources confirm or add. If the article is empty or behind a paywall, "
                    + "use the summary and the other sources and say so. For further questions on this topic use web_search with the names written as in the title.");
            },
        };
    }

    private static async Task<string> Safe(Func<Task<string>> get)
    {
        try { return await get(); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or TaskCanceledException) { return ""; }
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}
