using AL76.Core.Config;

namespace AL76.Core.Accounts;

/// <summary>The two kinds of account AL-76 can use, with their sign-in and their data; a source exists only while connected.</summary>
public sealed class AccountSet
{
    private readonly AppConfig config;
    /// <summary>Where the tokens are kept; the portal modules keep their passes here too.</summary>
    public ISecretStore Secrets { get; }
    public OAuthClient Microsoft { get; }
    public OAuthClient Google { get; }
    private readonly GraphSource graph;
    private readonly GoogleSource google;

    public AccountSet(AppConfig config, ISecretStore secrets, HttpClient? http = null, string? graphUrl = null, string? googleRoot = null)
    {
        this.config = config;
        Secrets = secrets;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        Microsoft = new OAuthClient(OAuthProvider.Microsoft, () => config.MicrosoftClientId.Trim(), () => "", secrets, http);
        Google = new OAuthClient(OAuthProvider.Google, () => config.GoogleClientId.Trim(), () => config.GoogleClientSecret.Trim(), secrets, http);
        graph = new GraphSource(Microsoft, http, graphUrl);
        google = new GoogleSource(Google, http, googleRoot);
    }

    public OAuthClient? Client(string id) => id switch { "microsoft" => Microsoft, "google" => Google, _ => null };

    public IOrganizerSource? Organizer(string id) => id switch
    {
        "microsoft" when Microsoft.Connected => graph,
        "google" when Google.Connected => google,
        _ => null,
    };

    public IMailSource? Mail() => config.MailAccount switch
    {
        "microsoft" when Microsoft.Connected => graph,
        "google" when Google.Connected => google,
        _ => null,
    };

    /// <summary>Signs in and, for Microsoft, reads the address to show in the settings. Null = done.</summary>
    public async Task<string?> ConnectAsync(string id, Action<string> openBrowser, bool italian, CancellationToken ct)
    {
        var client = Client(id) ?? throw new ArgumentException(id);
        string? problem = await client.SignInAsync(openBrowser, italian, ct);
        if (problem is null && id == "microsoft" && client.AccountName.Length == 0)
        {
            try { client.SetAccountName(await graph.WhoAsync(ct)); } catch (AccountException) { }
        }
        // The first account connected also takes the mail, unless one was chosen.
        if (problem is null && config.MailAccount.Length == 0) config.MailAccount = id;
        return problem;
    }

    public void Disconnect(string id)
    {
        Client(id)?.Disconnect();
        if (config.MailAccount == id) config.MailAccount = "";
    }
}
