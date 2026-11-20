using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AL76.Core.Safety;

namespace AL76.Core.Web;

/// <summary>
/// Web search, page reading and weather, as on the Mac: free services without API keys (DuckDuckGo's HTML page,
/// wttr.in). Pages and results are other people's text: they come back marked as external content.
/// </summary>
public static partial class WebActions
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("it-IT,it;q=0.9,en;q=0.8");
        return c;
    }

    public sealed record Result(string Title, string Snippet, string Url);

    [GeneratedRegex(@"class=""result__a""[^>]*href=""([^""]+)""[^>]*>([\s\S]*?)</a>")]
    private static partial Regex TitleRegex();
    [GeneratedRegex(@"class=""result__snippet""[^>]*>([\s\S]*?)</a>")]
    private static partial Regex SnippetRegex();

    public static async Task<List<Result>> SearchAsync(string query, CancellationToken ct = default)
    {
        string html = await Http.GetStringAsync("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query), ct);
        return ParseSearch(html);
    }

    public static List<Result> ParseSearch(string html)
    {
        var titles = TitleRegex().Matches(html);
        var snippets = SnippetRegex().Matches(html);
        return titles.Select((t, i) => new Result(
                StripTags(t.Groups[2].Value),
                i < snippets.Count ? StripTags(snippets[i].Groups[1].Value) : "",
                DecodeDuckLink(t.Groups[1].Value)))
            // Ads go through DuckDuckGo's click counter (y.js): they are not results (17 November, Amazon first).
            .Where(r => !r.Url.Contains("duckduckgo.com/y.js") && !r.Url.Contains("ad_domain="))
            .Take(6).ToList();
    }

    public static async Task<string> ReadPageAsync(Uri url, CancellationToken ct = default)
    {
        string html = await Http.GetStringAsync(url, ct);
        html = Regex.Replace(html, @"<(script|style|nav|footer|header|svg)[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
        string text = Regex.Replace(StripTags(html), @"\s{2,}", " ");
        return text.Length > 5000 ? text[..5000] : text;
    }

    /// <summary>
    /// The weather from wttr.in: now and the next three days, in the user's language. An empty place is the
    /// place of the internet connection.
    /// </summary>
    public static async Task<string> WeatherAsync(string place, bool italian, CancellationToken ct = default)
    {
        string json = await Http.GetStringAsync($"https://wttr.in/{Uri.EscapeDataString(place.Trim())}?format=j1&lang={(italian ? "it" : "en")}", ct);
        return FormatWeather(JsonNode.Parse(json) as JsonObject, place, italian);
    }

    public static string FormatWeather(JsonObject? json, string place, bool italian)
    {
        var current = json?["current_condition"]?[0];
        if (current is null) return italian ? "Meteo non disponibile." : "Weather not available.";
        // The city the user named, not wttr.in's nearest district ("Bologna" came back as "Santa Viola").
        string area = place.Trim().Length > 0 ? place.Trim() : json!["nearest_area"]?[0]?["areaName"]?[0]?["value"]?.ToString() ?? "";
        string desc = (italian ? current["lang_it"]?[0]?["value"]?.ToString() : null) ?? current["weatherDesc"]?[0]?["value"]?.ToString() ?? "";
        string text = italian
            ? $"Meteo a {area}: {desc}, {current["temp_C"]} gradi (percepiti {current["FeelsLikeC"]}), umidità {current["humidity"]} per cento, vento {current["windspeedKmph"]} chilometri orari."
            : $"Weather in {area}: {desc}, {current["temp_C"]} degrees (feels like {current["FeelsLikeC"]}), humidity {current["humidity"]} percent, wind {current["windspeedKmph"]} kilometres per hour.";
        string[] labels = italian ? ["Oggi", "Domani", "Dopodomani"] : ["Today", "Tomorrow", "The day after"];
        int i = 0;
        foreach (var day in (json!["weather"] as JsonArray ?? []).Take(3))
        {
            int rain = (day?["hourly"] as JsonArray ?? []).Select(h => int.TryParse(h?["chanceofrain"]?.ToString(), out int r) ? r : 0).DefaultIfEmpty(0).Max();
            text += italian
                ? $"\n{labels[i]}: minima {day?["mintempC"]} gradi, massima {day?["maxtempC"]} gradi, probabilità di pioggia fino al {rain} per cento."
                : $"\n{labels[i]}: low {day?["mintempC"]}, high {day?["maxtempC"]} degrees, chance of rain up to {rain} percent.";
            i++;
        }
        return text;
    }

    public static string StripTags(string html)
    {
        string s = Regex.Replace(html, "<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>DuckDuckGo's links go through its redirect: the real address is in "uddg".</summary>
    public static string DecodeDuckLink(string href)
    {
        string link = WebUtility.HtmlDecode(href.StartsWith("//") ? "https:" + href : href);
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return link;
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
            if (part.StartsWith("uddg=")) return Uri.UnescapeDataString(part[5..]);
        return link;
    }

    public static string FormatResults(string query, List<Result> results) =>
        results.Count == 0 ? $"No results for {query}."
            : Security.External("web search", string.Join("\n", results.Select((r, i) => $"{i + 1}. {r.Title} — {r.Snippet} [{r.Url}]")));
}
