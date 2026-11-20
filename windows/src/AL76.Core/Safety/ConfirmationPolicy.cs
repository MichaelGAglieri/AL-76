using AL76.Core.Voice;

namespace AL76.Core.Safety;

/// <summary>What an action can do that a person may want to approve first.</summary>
[Flags]
public enum Risk
{
    None = 0,
    Purchase = 1,
    Password = 2,
    SendEmail = 4,
    /// <summary>A chat message, a post, a calendar invite to other people.</summary>
    SendMessage = 8,
    Delete = 16,
    /// <summary>Closing apps or windows, where unsaved work can be lost.</summary>
    Close = 32,
    /// <summary>Opening a program, a script or an installer.</summary>
    RunProgram = 64,
    Overwrite = 128,
    /// <summary>A web address that came from no search and that the user did not name.</summary>
    UnknownSite = 256,
}

/// <summary>
/// When AL-76 asks for the "yes" before acting. Two levels, chosen in the settings:
/// <list type="bullet">
/// <item><b>Essenziale</b> (default, the Mac rule of 8 November 2026): only buying, passwords and sending email,
/// plus the security checks that are never optional (programs, unknown sites).</item>
/// <item><b>Prudente</b>: also before deleting, closing, overwriting and sending any message.</item>
/// </list>
/// </summary>
public static class ConfirmationPolicy
{
    /// <summary>Asked at every level: money, passwords, email, code that runs, sites no one asked for.</summary>
    public const Risk Always = Risk.Purchase | Risk.Password | Risk.SendEmail | Risk.RunProgram | Risk.UnknownSite;
    public const Risk CarefulExtra = Risk.SendMessage | Risk.Delete | Risk.Close | Risk.Overwrite;

    public static bool NeedsYes(Risk risk, bool careful) =>
        (risk & (careful ? Always | CarefulExtra : Always)) != 0;

    private static readonly string[] PurchaseStems =
        ["compra", "acquista", "acquisto", "paga", "pagamento", "abbonati", "abbonamento", "ordina",
         "buy", "purchase", "payment", "checkout", "subscribe", "order"];
    private static readonly HashSet<string> PurchaseExact = ["pay"];
    private static readonly string[] PasswordWords = ["password", "passcode", "parola chiave", "pin"];
    private static readonly string[] SendStems = ["invia", "manda", "spedisci", "send"];
    private static readonly HashSet<string> SentFolders = ["inviati", "inviato", "inviata", "inviate", "sent"];
    private static readonly string[] DeleteStems = ["elimina", "cancella", "rimuovi", "cestina", "delete", "remove", "trash", "erase"];
    /// <summary>Mail apps on Windows: there "Invia" sends an email.</summary>
    private static readonly string[] MailApps = ["outlook", "olk", "hxoutlook", "mail", "gmail", "thunderbird", "mailbird", "em client", "spark"];

    private static string[] Words(string text) =>
        TextTools.NormalizePhrase(text.Replace('\'', ' ')).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool Stem(string word, string[] stems) => stems.Any(s => word.StartsWith(s) && word.Length - s.Length <= 4);

    public static bool IsMail(string appIdentity)
    {
        string t = " " + appIdentity.ToLowerInvariant() + " ";
        return MailApps.Any(m => t.Contains(m));
    }

    /// <summary>The risk of pressing a button with this label inside an app (phase 4 uses it for clicks).</summary>
    public static Risk ButtonRisk(string label, string appIdentity)
    {
        var words = Words(label);
        string lower = label.ToLowerInvariant();
        Risk risk = Risk.None;
        if (words.Any(w => PurchaseExact.Contains(w) || Stem(w, PurchaseStems))) risk |= Risk.Purchase;
        if (PasswordWords.Any(p => p == "pin" ? words.Contains("pin") : lower.Contains(p))) risk |= Risk.Password;
        bool sends = words.Any(w => !SentFolders.Contains(w) && Stem(w, SendStems));
        if (sends) risk |= IsMail(appIdentity) ? Risk.SendEmail : Risk.SendMessage;
        if (words.Any(w => Stem(w, DeleteStems))) risk |= Risk.Delete;
        return risk;
    }

    /// <summary>A short answer to "lo faccio?": yes, no, or something else (a new request).</summary>
    public enum Reply { Yes, No, Unclear }

    private static readonly HashSet<string> NoWords = ["no", "non", "annulla", "lascia", "cancel", "dont", "don", "aspetta", "wait", "stop", "nope"];
    private static readonly HashSet<string> YesWords = ["si", "confermo", "conferma", "invia", "procedi", "vai", "certo", "ok", "okay", "fallo",
                                                         "yes", "confirm", "send", "sure", "yeah", "yep", "go", "perfetto", "esatto"];

    /// <summary>Only a short phrase counts: "assurdo è quando vede invia email", said to someone else, is not a yes (Mac, 2026).</summary>
    public static Reply Classify(string text)
    {
        var words = Words(text);
        if (words.Length == 0 || words.Length > 6) return Reply.Unclear;
        if (words.Any(NoWords.Contains)) return Reply.No;
        if (words.Any(YesWords.Contains)) return Reply.Yes;
        return Reply.Unclear;
    }
}
