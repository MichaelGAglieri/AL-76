using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AL76.Core.Diagnostics;
using AL76.Core.Maker;
using AL76.Core.Safety;
using AL76.Core.Voice;

namespace AL76.Core.Tools;

/// <summary>
/// send_message: a chat message in WhatsApp (or another chat app) in one call, instead of six or seven turns of
/// app_read, app_type and app_press with the brain (a tester waited a minute and a half for "I cannot", 26/11).
/// Same rules as the other app actions: forbidden apps, the local model, "basta", and the yes at the careful level.
/// </summary>
public sealed partial class AppControl
{
    private static readonly string[] MessageFieldWords = ["messaggio", "message", "scrivi", "digita", "type", "write", "compose", "componi"];

    /// <summary>The time a window gets to show what was asked (the app opening, the search results, the chat).</summary>
    public TimeSpan Patience { get; set; } = TimeSpan.FromSeconds(12);
    public TimeSpan Poll { get; set; } = TimeSpan.FromMilliseconds(400);

    public async Task<ToolOutcome> SendMessageAsync(JsonObject a, ToolContext c, Func<string, CancellationToken, Task<PcResult>> open, CancellationToken ct)
    {
        string app = a.Str("app") is { Length: > 0 } named ? named.Trim() : "WhatsApp";
        string to = a.Str("to").Trim();
        string text = a.Str("text").Trim();
        if (to.Length == 0) return ToolOutcome.Ok(L(c, "Manca il destinatario.", "The recipient is missing."));
        if (text.Length == 0) return ToolOutcome.Ok(L(c, "Manca il testo del messaggio.", "The message text is missing."));

        // The app open, or opened now (the same check as every other app action once it is there).
        if (driver.Find(app) is null && !Halted && enabled() && c.Brain != "local" && !IsBlocked(app))
        {
            var opened = await open(app, ct);
            if (!opened.Ok) return ToolOutcome.Ok(L(c, $"Non riesco ad aprire {app}: {opened.ForModel}", $"Could not open {app}: {opened.ForModel}"));
            await Until(() => Task.FromResult(driver.Find(app) is not null), ct);
        }
        var (w, problem) = Target(app, c);
        if (w is null) return ToolOutcome.Ok(problem!);

        // 1. The search field (the app may still be loading its chats).
        // A few seconds for the app to show its controls; WhatsApp for Windows never does, the screen path follows.
        var snap = await Read(w, s => SearchField(s) is not null, ct, Patience / 3);
        if (BlockedPage(w.Title + " " + snap.Url) is string page)
            return ToolOutcome.Ok(L(c, $"La pagina aperta ({page}) è di quelle che AL-76 non può controllare.", $"The open page ({page}) is one AL-76 may not control."));
        // WhatsApp for Windows shows UI Automation only its window buttons (a tester's PC, 26/11): then by what is on screen.
        if (SearchField(snap) is not { } search) return await SendByScreenAsync(w, to, text, a, c, ct);
        if (Halted) return ToolOutcome.Ok(L(c, "Interrotto dall'utente.", "Stopped by the user."));
        if (await driver.TypeAsync(w, search, to, false, ct) is string why) return ToolOutcome.Ok(why);

        // 2. The chat with that name among the results.
        List<UiNode> found = [];
        snap = await Read(w, s => (found = Chats(s, to, search)).Count > 0, ct);
        if (found.Count == 0)
            return ToolOutcome.Ok(L(c, $"In {w.Name} non trovo una chat con «{to}». Chiedi all'utente il nome come è salvato nella rubrica.",
                                        $"No chat with \"{to}\" in {w.Name}. Ask the user for the name as saved in their contacts."));
        var names = found.Select(n => ChatName(n.Label)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count > 1 && !names.Any(n => TextTools.NormalizePhrase(n) == TextTools.NormalizePhrase(to)))
            return ToolOutcome.Ok(L(c, $"In {w.Name} ci sono più chat per «{to}»: {string.Join(", ", names.Take(4))}. Chiedi all'utente quale e rimanda send_message con il nome intero.",
                                        $"Several chats match \"{to}\" in {w.Name}: {string.Join(", ", names.Take(4))}. Ask the user which one and call send_message again with the full name."));
        var chat = found.FirstOrDefault(n => TextTools.NormalizePhrase(ChatName(n.Label)) == TextTools.NormalizePhrase(to)) ?? found[0];
        string who = ChatName(chat.Label);
        var before = snap.Nodes.Where(n => n.IsField).Select(n => n.Label).ToHashSet();

        // 3. Into the chat, then the message field.
        if (!await driver.PressAsync(w, chat, ct))
            return ToolOutcome.Ok(L(c, $"Non riesco ad aprire la chat con {who}: fallo con app_read e app_press.", $"Could not open the chat with {who}: do it with app_read and app_press."));
        snap = await Read(w, s => MessageField(s, before) is not null, ct);
        if (MessageField(snap, before) is not { } box)
            return ToolOutcome.Ok(L(c, $"Ho aperto la chat con {who} ma non trovo il campo del messaggio: continua con app_read.",
                                        $"I opened the chat with {who} but cannot find the message field: go on with app_read."));

        // 4. Typed and sent (the yes only at the careful level, like every message).
        return await Guard(w, "invia messaggio", Risk.SendMessage, a.Bool("consequential") == true,
            L(c, $"Mandare a {who} su {w.Name}: «{Clip(text, 80)}»", $"Send {who} on {w.Name}: \"{Clip(text, 80)}\""), async ct2 =>
            {
                if (Halted) return L(c, "Interrotto dall'utente.", "Stopped by the user.");
                if (await driver.TypeAsync(w, box, text, true, ct2) is string failed) return failed;
                Log.Audit("app: messaggio", $"{text.Length} caratteri a una chat di {w.Name}");
                string probe = TextTools.NormalizePhrase(Clip(text, 40));
                var after = await Read(w, s => s.Nodes.Any(n => !n.IsField && TextTools.NormalizePhrase(n.Label).Contains(probe)), ct2);
                bool seen = after.Nodes.Any(n => !n.IsField && TextTools.NormalizePhrase(n.Label).Contains(probe));
                return seen
                    ? L(c, $"Messaggio mandato a {who} su {w.Name}.", $"Message sent to {who} on {w.Name}.")
                    : L(c, $"Scritto a {who} e dato Invio, ma non vedo ancora il messaggio nella chat: controlla con app_read.",
                           $"Typed to {who} and pressed Enter, but the message does not show in the chat yet: check with app_read.");
            }, ct);
    }

    /// <summary>
    /// The same steps by what the window shows, read with Windows' OCR: the "Cerca" box clicked and the name typed, the
    /// chat clicked among the results, its name checked at the top of the open chat, the text typed in "Scrivi un
    /// messaggio" and Enter. Nothing is sent unless the open chat shows the name.
    /// </summary>
    private async Task<ToolOutcome> SendByScreenAsync(AppWindow w, string to, string text, JsonObject a, ToolContext c, CancellationToken ct)
    {
        string manual = L(c, " Fallo con app_look, app_click_at e app_type senza element.", " Do it with app_look, app_click_at and app_type without element.");
        var seen = await Screen(w, t => SearchLine(t) is not null, ct);
        if (seen is null) return ToolOutcome.Ok(L(c, $"Non riesco a leggere la finestra di {w.Name}.", $"I cannot read {w.Name}'s window.") + manual);
        if (SearchLine(seen) is not { } box)
            return ToolOutcome.Ok(L(c, $"In {w.Name} non vedo il campo «Cerca» (forse va prima collegato il telefono).", $"I do not see the search box in {w.Name} (the phone may need linking first).") + manual);
        if (Halted) return ToolOutcome.Ok(L(c, "Interrotto dall'utente.", "Stopped by the user."));
        if (await Click(w, seen, box, ct) is string why) return ToolOutcome.Ok(why);
        await Task.Delay(Poll, ct);
        if (await driver.TypeFocusedAsync(w, to, false, ct) is string why2) return ToolOutcome.Ok(why2);

        List<ScreenWord> found = [];
        seen = await Screen(w, t => (found = ChatLines(t, to, box)).Count > 0, ct);
        if (seen is null || found.Count == 0)
            return ToolOutcome.Ok(L(c, $"In {w.Name} non trovo una chat con «{to}». Chiedi all'utente il nome come è salvato nella rubrica.",
                                        $"No chat with \"{to}\" in {w.Name}. Ask the user for the name as saved in their contacts."));
        var names = found.Select(l => LineName(l.Text)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string wanted = TextTools.NormalizePhrase(to);
        if (names.Count > 1 && !names.Any(n => TextTools.NormalizePhrase(n) == wanted))
            return ToolOutcome.Ok(L(c, $"In {w.Name} ci sono più chat per «{to}»: {string.Join(", ", names.Take(4))}. Chiedi all'utente quale e rimanda send_message con il nome intero.",
                                        $"Several chats match \"{to}\" in {w.Name}: {string.Join(", ", names.Take(4))}. Ask the user which one and call send_message again with the full name."));
        var chat = found.FirstOrDefault(l => TextTools.NormalizePhrase(LineName(l.Text)) == wanted) ?? found[0];
        string who = LineName(chat.Text);
        if (Halted) return ToolOutcome.Ok(L(c, "Interrotto dall'utente.", "Stopped by the user."));
        if (await Click(w, seen, chat, ct) is string why3) return ToolOutcome.Ok(why3);

        // The open chat: its name at the top, right of the list, and the message box at the bottom.
        string whoNorm = TextTools.NormalizePhrase(who);
        bool Header(WindowText t) => t.Lines.Any(l => l.Left > chat.Left + chat.Width && l.Top < t.Frame.Top + t.Frame.Height * 0.3
                                                      && TextTools.NormalizePhrase(LineName(l.Text)).StartsWith(whoNorm));
        seen = await Screen(w, t => Header(t) && Composer(t) is not null, ct);
        if (seen is null || !Header(seen))
            return ToolOutcome.Ok(L(c, $"Ho cliccato la chat con {who} ma non vedo il suo nome in alto: non mando niente.", $"I clicked the chat with {who} but do not see the name at the top: nothing sent.") + manual);
        if (Composer(seen) is not { } composer)
            return ToolOutcome.Ok(L(c, $"Ho aperto la chat con {who} ma non vedo «Scrivi un messaggio».", $"I opened the chat with {who} but do not see the message box.") + manual);

        var opened = seen;
        return await Guard(w, "invia messaggio", Risk.SendMessage, a.Bool("consequential") == true,
            L(c, $"Mandare a {who} su {w.Name}: «{Clip(text, 80)}»", $"Send {who} on {w.Name}: \"{Clip(text, 80)}\""), async ct2 =>
            {
                if (Halted) return L(c, "Interrotto dall'utente.", "Stopped by the user.");
                if (await Click(w, opened, composer, ct2) is string failed) return failed;
                await Task.Delay(Poll, ct2);
                if (await driver.TypeFocusedAsync(w, text, true, ct2) is string failed2) return failed2;
                Log.Audit("app: messaggio", $"{text.Length} caratteri a una chat di {w.Name} (dallo schermo)");
                string probe = TextTools.NormalizePhrase(Clip(text, 30));
                bool Shown(WindowText t) => t.Lines.Any(l => l.Top < composer.Top && TextTools.NormalizePhrase(l.Text).Contains(probe));
                var after = await Screen(w, Shown, ct2);
                return after is not null && Shown(after)
                    ? L(c, $"Messaggio mandato a {who} su {w.Name}.", $"Message sent to {who} on {w.Name}.")
                    : L(c, $"Scritto a {who} e dato Invio, ma non vedo ancora il messaggio nella chat: controlla con app_look.",
                           $"Typed to {who} and pressed Enter, but the message does not show in the chat yet: check with app_look.");
            }, ct);
    }

    /// <summary>OCR reads until the window shows what is wanted, or the patience ends; null when it cannot read at all.</summary>
    private async Task<WindowText?> Screen(AppWindow w, Func<WindowText, bool> wanted, CancellationToken ct)
    {
        WindowText? seen = null;
        await Until(async () => (seen = await driver.ReadTextAsync(w, ct)) is null || wanted(seen), ct);
        return seen;
    }

    private Task<string?> Click(AppWindow w, WindowText seen, ScreenWord line, CancellationToken ct) =>
        driver.ClickAsync(w, new Capture([], (int)seen.Frame.Width, (int)seen.Frame.Height, w.Name, w.Handle, seen.Frame), line.MidX, line.MidY, ct);

    private static readonly string[] SearchWords = ["cerca", "search", "ricerca"];
    private static readonly string[] ComposerWords = ["scrivi un messaggio", "type a message", "scrivi", "digita un messaggio", "messaggio", "message"];

    /// <summary>The chat search box: the highest line that starts with "Cerca", in the left half (the chat list).</summary>
    public static ScreenWord? SearchLine(WindowText t) =>
        t.Lines.Where(l => l.MidX < t.Frame.Left + t.Frame.Width * 0.5)
               .Where(l => TextTools.NormalizePhrase(l.Text).Split(' ').Take(2).Any(SearchWords.Contains))
               .OrderBy(l => l.Top).FirstOrDefault();

    /// <summary>The message box: a line in the lower right part (the open chat) that reads like "Scrivi un messaggio".</summary>
    public static ScreenWord? Composer(WindowText t) =>
        t.Lines.Where(l => l.Top > t.Frame.Top + t.Frame.Height * 0.6 && l.MidX > t.Frame.Left + t.Frame.Width * 0.35)
               .Where(l => ComposerWords.Any(k => TextTools.NormalizePhrase(l.Text).StartsWith(k)))
               .OrderByDescending(l => l.Top).FirstOrDefault();

    /// <summary>The results under the search box, in the chat list, whose name matches what the user said.</summary>
    public static List<ScreenWord> ChatLines(WindowText t, string to, ScreenWord box)
    {
        string wanted = TextTools.NormalizePhrase(to);
        var words = wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        return t.Lines.Where(l => l.Top > box.Top + box.Height && l.MidX < t.Frame.Left + t.Frame.Width * 0.5)
            .Where(l =>
            {
                string name = TextTools.NormalizePhrase(LineName(l.Text));
                if (name.Length == 0) return false;
                if (name == wanted || name.StartsWith(wanted + " ")) return true;
                var nameWords = name.Split(' ');
                return words.All(nameWords.Contains);
            })
            .OrderBy(l => l.Top).ToList();
    }

    /// <summary>"Marco Rossi 10:32" or "Marco Rossi ieri" → "Marco Rossi": the time or day the list shows next to the name.</summary>
    public static string LineName(string line)
    {
        string s = ChatName(line);
        s = Regex.Replace(s, @"\s+(\d{1,2}[:.]\d{2}|\d{1,2}/\d{1,2}(/\d{2,4})?|ieri|oggi|yesterday|today|lunedì|martedì|mercoledì|giovedì|venerdì|sabato|domenica|monday|tuesday|wednesday|thursday|friday|saturday|sunday)$", "", RegexOptions.IgnoreCase);
        return s.Trim();
    }

    private async Task Until(Func<Task<bool>> ready, CancellationToken ct, TimeSpan? patience = null)
    {
        var end = DateTime.UtcNow + (patience ?? Patience);
        while (!await ready() && DateTime.UtcNow < end) await Task.Delay(Poll, ct);
    }

    /// <summary>Reads until the window shows what is wanted, or the patience ends: the last read either way.</summary>
    private async Task<UiSnapshot> Read(AppWindow w, Func<UiSnapshot, bool> wanted, CancellationToken ct, TimeSpan? patience = null)
    {
        UiSnapshot snap = new([]);
        await Until(async () => wanted(snap = await driver.ReadAsync(w, false, ct)), ct, patience);
        elementsPid = -1;   // the numbers of an earlier app_read are no longer valid
        return snap;
    }

    private static UiNode? SearchField(UiSnapshot s) => s.Nodes.FirstOrDefault(n => n.IsField && n.Search && !n.Password);

    /// <summary>The field to write in once the chat is open: one named like a message box, or else the only new field.</summary>
    private static UiNode? MessageField(UiSnapshot s, HashSet<string> before)
    {
        var fields = s.Nodes.Where(n => n.IsField && !n.Search && !n.Password && n.Enabled).ToList();
        return fields.FirstOrDefault(n => MessageFieldWords.Any(k => n.Label.Contains(k, StringComparison.OrdinalIgnoreCase)))
               ?? (fields.Where(n => !before.Contains(n.Label)).ToList() is [var only] ? only : null);
    }

    /// <summary>"Marco Rossi, 10:32, Ci vediamo dopo" → "Marco Rossi".</summary>
    public static string ChatName(string label)
    {
        string first = label.Split('\n')[0];
        int comma = first.IndexOf(',');
        return (comma > 0 ? first[..comma] : first).Trim();
    }

    /// <summary>The results whose name starts with what the user said ("Marco" → "Marco Rossi"), or holds all its words
    /// ("Rossi Marco"); the search field and plain text are not chats.</summary>
    public static List<UiNode> Chats(UiSnapshot s, string to, UiNode search)
    {
        string wanted = TextTools.NormalizePhrase(to);
        var words = wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        return s.Nodes.Where(n => n != search && n.Kind is "item" or "button" or "link" && n.Enabled)
            .Where(n =>
            {
                string name = TextTools.NormalizePhrase(ChatName(n.Label));
                if (name.Length == 0) return false;
                if (name == wanted || name.StartsWith(wanted + " ")) return true;
                var nameWords = name.Split(' ');
                return words.All(nameWords.Contains);
            })
            .OrderByDescending(n => TextTools.NormalizePhrase(ChatName(n.Label)) == wanted)
            .ThenBy(n => n.Kind == "item" ? 0 : 1)
            .ToList();
    }
}
