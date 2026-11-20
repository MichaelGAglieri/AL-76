using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AL76.Core.Diagnostics;

namespace AL76.Core.Accounts;

/// <summary>Where the tokens of the accounts are kept: on Windows encrypted for the user (DPAPI), in the tests in memory.</summary>
public interface ISecretStore
{
    string? Read(string name);
    void Write(string name, string? value);
}

public sealed class MemorySecrets : ISecretStore
{
    private readonly Dictionary<string, string> values = [];
    public string? Read(string name) { lock (values) return values.GetValueOrDefault(name); }
    public void Write(string name, string? value) { lock (values) { if (value is null) values.Remove(name); else values[name] = value; } }
}

/// <summary>A provider's OAuth endpoints and the permissions AL-76 asks for.</summary>
public sealed record OAuthProvider(string Id, string Name, string AuthorizeUrl, string TokenUrl, string[] Scopes, bool NeedsSecret)
{
    /// <summary>Microsoft personal and work accounts: calendar, To Do, mail, contacts (Graph). Public client, no secret.</summary>
    public static readonly OAuthProvider Microsoft = new("microsoft", "Microsoft",
        "https://login.microsoftonline.com/common/oauth2/v2.0/authorize", "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        ["offline_access", "User.Read", "Calendars.ReadWrite", "Tasks.ReadWrite", "Mail.ReadWrite", "Mail.Send", "Contacts.Read"], false);

    /// <summary>Google: Calendar, Tasks, Gmail, contacts. A "desktop app" client has a secret that is not secret (Google's own rule).</summary>
    public static readonly OAuthProvider Google = new("google", "Google",
        "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token",
        ["openid", "email", "https://www.googleapis.com/auth/calendar", "https://www.googleapis.com/auth/tasks",
         "https://www.googleapis.com/auth/gmail.modify", "https://www.googleapis.com/auth/gmail.send",
         "https://www.googleapis.com/auth/contacts.readonly"], true);

    public static OAuthProvider? ById(string id) => id switch { "microsoft" => Microsoft, "google" => Google, _ => null };
}

/// <summary>The tokens of one account. Saved as JSON in the secret store under "account.&lt;provider&gt;".</summary>
public sealed record OAuthTokens(string AccessToken, string RefreshToken, DateTime Expires, string Account = "");

/// <summary>
/// Sign-in in the user's browser (authorization code with PKCE and a loopback address, the way desktop apps are
/// meant to do it: AL-76 never sees the password), then tokens renewed by themselves. One instance per provider.
/// </summary>
public sealed class OAuthClient(OAuthProvider provider, Func<string> clientId, Func<string> clientSecret, ISecretStore secrets, HttpClient http)
{
    private readonly SemaphoreSlim refreshing = new(1, 1);
    public OAuthProvider Provider => provider;
    private string Key => "account." + provider.Id;

    public bool HasClient => clientId().Length > 0 && (!provider.NeedsSecret || clientSecret().Length > 0);
    public bool Connected => Tokens is not null;

    public OAuthTokens? Tokens
    {
        get
        {
            try { return secrets.Read(Key) is { } json ? JsonSerializer.Deserialize<OAuthTokens>(json) : null; }
            catch (JsonException) { return null; }
        }
        private set => secrets.Write(Key, value is null ? null : JsonSerializer.Serialize(value));
    }

    public string AccountName => Tokens?.Account ?? "";

    public void Disconnect() => Tokens = null;

    public static (string verifier, string challenge) Pkce()
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string AuthorizeUrl(string redirect, string challenge, string state) =>
        provider.AuthorizeUrl + "?" + string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = clientId(), ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["scope"] = string.Join(' ', provider.Scopes), ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            ["state"] = state, ["prompt"] = "select_account",
            // Google gives a refresh token only with these two.
            ["access_type"] = "offline",
        }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    /// <summary>
    /// The whole sign-in: a one-shot listener on 127.0.0.1, the provider's page opened in the browser, the code
    /// taken from the redirect and changed into tokens. Returns null when it worked, else what went wrong.
    /// </summary>
    public async Task<string?> SignInAsync(Action<string> openBrowser, bool italian, CancellationToken ct)
    {
        if (!HasClient) return italian ? "Manca l'ID dell'app (vedi la guida nella pagina Account)." : "The app ID is missing (see the guide on the Accounts page).";
        int port = FreePort();
        // Microsoft wants the address registered in Azure, "http://localhost" (any port); Google the loopback IP.
        string redirect = provider.Id == "microsoft" ? $"http://localhost:{port}/" : $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();
        var (verifier, challenge) = Pkce();
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));
        openBrowser(AuthorizeUrl(redirect, challenge, state));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        HttpListenerContext context;
        try { context = await listener.GetContextAsync().WaitAsync(timeout.Token); }
        catch (OperationCanceledException) { return italian ? "Accesso non completato entro 5 minuti." : "Sign-in not completed within 5 minutes."; }
        var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "");
        string page = italian ? "<h2>AL-76: accesso fatto. Puoi chiudere questa scheda.</h2>" : "<h2>AL-76: signed in. You can close this tab.</h2>";
        string? problem = null;
        if (query["state"] != state) problem = italian ? "Risposta del browser non valida." : "Invalid browser reply.";
        else if (query["error"] is { } error) problem = (italian ? "Accesso rifiutato: " : "Sign-in refused: ") + error;
        else if (query["code"] is not { Length: > 0 } code) problem = italian ? "Nessun codice nella risposta." : "No code in the reply.";
        else
        {
            problem = await ExchangeAsync(new()
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = verifier,
            }, null, italian, ct);
        }
        if (problem is not null) page = $"<h2>AL-76: {WebUtility.HtmlEncode(problem)}</h2>";
        byte[] body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><body style=\"font-family:sans-serif;padding:40px\">{page}</body>");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(body, ct);
        context.Response.Close();
        return problem;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Calls the token endpoint; on success saves the tokens. Null = done.</summary>
    public async Task<string?> ExchangeAsync(Dictionary<string, string> form, OAuthTokens? old, bool italian, CancellationToken ct)
    {
        form["client_id"] = clientId();
        if (provider.NeedsSecret) form["client_secret"] = clientSecret();
        using var response = await http.PostAsync(provider.TokenUrl, new FormUrlEncodedContent(form), ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        JsonObject? json = null;
        try { json = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { }
        if (!response.IsSuccessStatusCode || json?["access_token"]?.GetValue<string>() is not { } access)
        {
            string why = json?["error_description"]?.ToString() ?? json?["error"]?.ToString() ?? $"HTTP {(int)response.StatusCode}";
            Log.Info($"account {provider.Id}: token rifiutato: {why}");
            return (italian ? "Il servizio ha rifiutato l'accesso: " : "The service refused: ") + why;
        }
        string refresh = json["refresh_token"]?.GetValue<string>() ?? old?.RefreshToken ?? "";
        int seconds = json["expires_in"]?.GetValue<int>() ?? 3600;
        string account = old?.Account ?? AccountFromIdToken(json["id_token"]?.GetValue<string>()) ?? "";
        Tokens = new OAuthTokens(access, refresh, DateTime.UtcNow.AddSeconds(seconds - 60), account);
        return null;
    }

    /// <summary>The e-mail address in an OpenID token (no signature check: it is only a label for the settings).</summary>
    public static string? AccountFromIdToken(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            string b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var claims = JsonNode.Parse(Convert.FromBase64String(b64)) as JsonObject;
            return claims?["email"]?.ToString() ?? claims?["preferred_username"]?.ToString();
        }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
    }

    /// <summary>A valid access token, renewed when it is about to expire; null when not connected or renewal failed.</summary>
    public async Task<string?> AccessTokenAsync(CancellationToken ct)
    {
        if (Tokens is not { } t) return null;
        if (t.Expires > DateTime.UtcNow) return t.AccessToken;
        await refreshing.WaitAsync(ct);
        try
        {
            if (Tokens is not { } now) return null;
            if (now.Expires > DateTime.UtcNow) return now.AccessToken;
            if (now.RefreshToken.Length == 0) return null;
            string? problem = await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = now.RefreshToken }, now, true, ct);
            return problem is null ? Tokens?.AccessToken : null;
        }
        finally { refreshing.Release(); }
    }

    /// <summary>The account label after sign-in, if the id token did not carry it (Microsoft: /me).</summary>
    public void SetAccountName(string name)
    {
        if (Tokens is { } t && name.Length > 0) Tokens = t with { Account = name };
    }
}
