using AL76.Core.Web;

namespace AL76.Core.Tests;

public class NewsFeedTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 9, 0, 0);

    private static string Rss(params (string title, string link, string date, string summary)[] items) =>
        "<?xml version=\"1.0\"?><rss><channel><title>x</title>"
        + string.Concat(items.Select(i => $"<item><title><![CDATA[{i.title}]]></title><link>{i.link}</link><pubDate>{i.date}</pubDate><description>{i.summary}</description></item>"))
        + "</channel></rss>";

    private static string R(DateTime local) => local.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " +0000";

    [Fact]
    public void ParsesRssAtomAndSkyGuid()
    {
        var rss = NewsFeed.Parse(Rss(("Un robot umanoide &amp; amici", "https://a.it/1", "Thu, 19 Nov 2026 07:30:00 +0000", "<p>Testo <b>breve</b></p>")), new("A", "https://a.it/rss"));
        Assert.Single(rss);
        Assert.Equal("Un robot umanoide & amici", rss[0].Title);
        Assert.Equal("Testo breve", rss[0].Summary);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 7, 30, 0, TimeSpan.Zero).LocalDateTime, rss[0].Date);

        var atom = NewsFeed.Parse("<feed xmlns=\"http://www.w3.org/2005/Atom\"><entry><title>Nuovo chip</title><link rel=\"alternate\" href=\"https://b.com/2\"/>"
                                  + "<published>2026-11-19T06:00:00Z</published><summary>s</summary></entry></feed>", new("B", "https://b.com/feed"));
        Assert.Equal("https://b.com/2", Assert.Single(atom).Link);

        var sky = NewsFeed.Parse("<rss><channel><item><title>Titolo</title><guid>https://tg24.sky.it/x</guid><pubDate>gio, 19 nov 2026 08:45:00 GMT</pubDate></item></channel></rss>",
                                 new("Sky TG24", "https://tg24.sky.it/rss"));
        Assert.Equal("https://tg24.sky.it/x", Assert.Single(sky).Link);
    }

    [Fact]
    public void BrokenFeedGivesNothing()
    {
        Assert.Empty(NewsFeed.Parse("<rss><channel><item><title>x", new("A", "https://a.it")));
        Assert.Empty(NewsFeed.Parse("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><rss><item><title>&e;</title></item></rss>", new("A", "https://a.it")));
    }

    [Fact]
    public void RanksInterestsDropsShoppingAndMergesTwins()
    {
        var items = new List<NewsFeed.Item>
        {
            new("ANSA", "Robot umanoide cammina da solo in fabbrica", "", "https://ansa.it/1", Now.AddHours(-2)),
            new("Sky TG24", "In fabbrica il robot umanoide che cammina da solo", "", "https://sky.it/1", Now.AddHours(-1)),
            new("DDay", "Black Friday: sconti sugli smartphone", "", "https://dday.it/1", Now.AddHours(-1)),
            new("Wired", "OpenAI presenta un nuovo modello", "", "https://wired.com/1", Now.AddHours(-3)),
            new("ANSA", "Il meteo del weekend", "", "https://ansa.it/2", Now.AddHours(-1)),
            new("ANSA", "Vecchia notizia sui robot", "", "https://ansa.it/3", Now.AddDays(-3)),
        };
        var chosen = NewsFeed.Rank(items, Now, 36, 8, null, []);
        // The same story from two sites once (the fresher one), the other site named; no shopping, weather or old news.
        Assert.Equal(["In fabbrica il robot umanoide che cammina da solo", "OpenAI presenta un nuovo modello"], chosen.Select(i => i.Title));
        Assert.Equal(["ANSA"], chosen[0].AlsoOn);
    }

    [Fact]
    public void UserTopicsComeFirst()
    {
        var items = new List<NewsFeed.Item>
        {
            new("A", "Nuovo modello di intelligenza artificiale", "", "https://a/1", Now.AddHours(-1)),
            new("B", "Formula 1: la Ferrari vince in Brasile", "", "https://b/1", Now.AddHours(-1)),
        };
        Assert.Empty(NewsFeed.Rank(items, Now, 36, 8, null, []).Where(i => i.Source == "B"));
        Assert.Equal("B", NewsFeed.Rank(items, Now, 36, 8, null, ["Formula 1", "Ferrari"])[0].Source);
        Assert.Equal("B", Assert.Single(NewsFeed.Rank(items, Now, 36, 8, "ferrari", [])).Source);
    }

    [Fact]
    public async Task NeverTellsTheSameTwiceAndFindsMisheardNames()
    {
        string file = Path.Combine(Path.GetTempPath(), $"al76-news-{Guid.NewGuid():N}.json");
        string xml = Rss(("Amazon presenta Praxis-1, il robot per i magazzini", "https://a.it/praxis", R(Now.AddHours(-1)), "Un nuovo robot umanoide"),
                         ("Nvidia annuncia un chip per robot", "https://a.it/nvidia", R(Now.AddHours(-2)), ""),
                         ("Un drone autonomo consegna pacchi", "https://a.it/drone", R(Now.AddHours(-2)), ""));
        var feed = new NewsFeed(file, () => [new NewsSource("A", "https://a.it/rss")], () => [], (_, _) => Task.FromResult<string?>(xml));
        var first = await feed.LatestAsync(Now, 8);
        Assert.Equal(3, first.Count);
        Assert.Equal("Amazon presenta Praxis-1, il robot per i magazzini", feed.GivenItem("Proxis 1", Now)?.Title);
        Assert.Null(feed.GivenItem("ricetta della pasta", Now));
        // Asked again: nothing new; a new instance reads the file and still remembers.
        var again = new NewsFeed(file, () => [new NewsSource("A", "https://a.it/rss")], () => [], (_, _) => Task.FromResult<string?>(xml));
        Assert.Empty(await again.LatestAsync(Now.AddMinutes(20), 8));
        Assert.Equal(3, again.AlreadyGiven(Now.AddHours(1)).Count);
        Assert.Empty(again.AlreadyGiven(Now.AddDays(1)));
    }

    [Fact]
    public void SourcesFromSettingsLines()
    {
        Assert.Equal(new NewsSource("Il Post", "https://www.ilpost.it/feed/"), NewsSource.FromLine("Il Post | https://www.ilpost.it/feed/"));
        Assert.Equal("ilpost.it", NewsSource.FromLine("https://www.ilpost.it/feed/")!.Name);
        Assert.Null(NewsSource.FromLine("file:///c:/x"));
    }

    /// <summary>Only with AL76_LIVE=1: the real feeds, to see that they still answer and parse.</summary>
    [Fact]
    public async Task LiveFeeds()
    {
        if (Environment.GetEnvironmentVariable("AL76_LIVE") != "1") return;
        string file = Path.Combine(Path.GetTempPath(), $"al76-live-{Guid.NewGuid():N}.json");
        var feed = new NewsFeed(file, () => NewsFeed.DefaultSources, () => []);
        var items = await feed.LatestAsync(DateTime.Now, 8);
        foreach (var i in items) Console.WriteLine($"LIVE {i.Source} | {i.Date:dd/MM HH:mm} | {i.Title} | {i.Score:0.0}");
        Assert.NotEmpty(items);
    }
}
