using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AL76.Core;
using AL76.Core.Voice;
using AL76.Core.Brains;
using AL76.Core.Config;
using AL76.Core.Diagnostics;
using AL76.Core.Hardware;
using AL76.Core.Localization;
using AL76.Core.Modules;
using AL76.Core.Setup;
using AL76.Platform;

namespace AL76.App;

/// <summary>What every page needs: the texts, the settings being edited, the running assistant and voice.</summary>
public sealed class PageContext
{
    public required Strings S { get; set; }
    public required AppConfig Draft { get; init; }
    public required Assistant Assistant { get; init; }
    public VoiceHost? Voice { get; init; }
    public required TierDecision Tier { get; init; }
    /// <summary>Something was edited (the settings save it at once; the setup saves when you go on).</summary>
    public Action Changed { get; init; } = () => { };
    /// <summary>The page must be drawn again (a profile with other fields, an area added…).</summary>
    public Action Redraw { get; set; } = () => { };
    public Action OpenSetup { get; init; } = () => { };
    public Action Restart { get; init; } = () => { };
    /// <summary>Saves the edits now (before signing in, so the app ID is in use).</summary>
    public Action Flush { get; init; } = () => { };
    /// <summary>The language of the running app (texts of the setup may already be in the newly chosen one).</summary>
    public required string RunningLanguage { get; init; }

    public string Name => Draft.AssistantName.Trim().Length > 0 ? Draft.AssistantName.Trim() : "AL-76";
    public string LocalModel => Draft.LocalModel.Length > 0 ? Draft.LocalModel : Tier.LocalBrain ?? "";
}

/// <summary>
/// The pages of the first-run setup and of the settings, in the Mac's order and words (Onboarding.swift,
/// SettingsView.swift), made generic. Each one returns its content; the window around it adds header,
/// buttons or the page list.
/// </summary>
public static class Pages
{
    // ── Welcome and language ────────────────────────────────────────────────────────────────

    public static UIElement Welcome(PageContext c, Action languageChanged)
    {
        var s = c.S;
        var panel = new StackPanel { Margin = new Thickness(20, 6, 20, 0) };
        var ring = new Grid { Width = 110, Height = 110, Margin = new Thickness(0, 0, 0, 14) };
        ring.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = Ui.Brush("Accent"), StrokeThickness = 10, Opacity = 0.25 });
        ring.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Stroke = Ui.Brush("Accent"), StrokeThickness = 2, Width = 84, Height = 84,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Color.FromRgb(0x4F, 0xDB, 0xFF), BlurRadius = 18, ShadowDepth = 0 },
        });
        ring.Children.Add(new TextBlock
        {
            Text = "AL", FontSize = 30, FontWeight = FontWeights.Bold, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            Foreground = Ui.Brush("Ice"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(ring);
        panel.Children.Add(new TextBlock { Text = s.Format("setup.welcome.hello", c.Name), FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), HorizontalAlignment = HorizontalAlignment.Center });
        var text = Ui.Caption(s.Format("setup.welcome.text", c.Draft.EffectiveWakePhrase(s.Language)), 13, new Thickness(0, 8, 0, 14));
        text.TextAlignment = TextAlignment.Center;
        panel.Children.Add(text);
        panel.Children.Add(Ui.Section("",
            Ui.Label("•  " + s["setup.welcome.f1"]), Ui.Label("•  " + s["setup.welcome.f2"]), Ui.Label("•  " + s["setup.welcome.f3"])));
        var langs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, key) in new[] { ("", "lang.auto"), ("it", "lang.it"), ("en", "lang.en") })
        {
            var r = Ui.Radio("lang", s[key], "", c.Draft.Language == id, () =>
            {
                c.Draft.Language = id;
                languageChanged();
            });
            r.Margin = new Thickness(0, 4, 24, 4);
            langs.Children.Add(r);
        }
        panel.Children.Add(Ui.Section(s["setup.welcome.language"], langs));
        var note = Ui.Caption(s["setup.welcome.note"]);
        note.TextAlignment = TextAlignment.Center;
        panel.Children.Add(note);
        return panel;
    }

    // ── Who you are ─────────────────────────────────────────────────────────────────────────

    public static UIElement You(PageContext c)
    {
        var s = c.S;
        var d = c.Draft;
        var profiles = new WrapPanel();
        foreach (var p in Enum.GetValues<UserProfile>())
        {
            var r = Ui.Radio("profile", s["profile." + p], "", d.Profile == p, () =>
            {
                d.Profile = p;
                SetupFlow.ProfileChanged(d, s.Language);
                c.Changed();
            });
            r.Margin = new Thickness(0, 4, 24, 4);
            profiles.Children.Add(r);
        }

        string[] titles = s.Language == "it" ? ["signore", "signora"] : ["sir", "madam"];
        var custom = new TextBox { Text = titles.Contains(d.Honorific) ? "" : d.Honorific };
        var address = new WrapPanel();
        void Add(string title, string value)
        {
            var r = Ui.Radio("address", title, "", d.Honorific == value, () =>
            {
                d.Honorific = value;
                custom.Text = "";
                c.Changed();
            });
            r.Margin = new Thickness(0, 4, 24, 4);
            address.Children.Add(r);
        }
        Add(s["setup.you.address.none"], "");
        foreach (var t in titles) Add(t, t);
        custom.TextChanged += (_, _) =>
        {
            if (custom.Text.Trim().Length == 0) return;
            d.Honorific = custom.Text.Trim();
            foreach (var r in address.Children.OfType<RadioButton>()) r.IsChecked = false;
            c.Changed();
        };

        return Ui.Stack(
            Ui.Intro(s["setup.step.You"], s["setup.you.text"]),
            Ui.Section(s["setup.you.name"], Ui.Field(s["setup.you.name.hint"], d.UserName, v => { d.UserName = v; c.Changed(); })),
            Ui.Section(s["setup.you.profile"], profiles),
            Ui.Section(s["setup.you.address"], address, Ui.Caption(s["setup.you.address.custom"], 12, new Thickness(0, 6, 0, 4)), custom,
                       Ui.Caption(s["setup.you.address.note"])),
            Ui.Section(s["setup.you.about"],
                Ui.Field(s["setup.you.about.hint"], d.About, v => { d.About = v; c.Changed(); }, multiline: true),
                Ui.Field(s["setup.you.interests.hint"], d.Interests, v => { d.Interests = v; c.Changed(); }),
                Ui.Field(s["setup.you.city"], d.City, v => { d.City = v; c.Changed(); }),
                Ui.Caption(s["setup.you.privacy"])));
    }

    // ── Study and work ──────────────────────────────────────────────────────────────────────

    public static UIElement StudyWork(PageContext c)
    {
        var s = c.S;
        var d = c.Draft;
        var page = Ui.Stack(Ui.Intro(s["setup.step.StudyWork"], s["setup.study.text"]));
        if (SetupFlow.AsksStudy(d.Profile))
            page.Children.Add(Ui.Section(s["setup.study.label"],
                Ui.Field(s["setup.study.hint"], d.Study, v => { d.Study = v; c.Changed(); }),
                Ui.Caption(s["setup.study.note"])));
        if (SetupFlow.AsksWork(d.Profile))
            page.Children.Add(Ui.Section(s["setup.work.label"],
                Ui.Field(s["setup.work.hint"], d.Work, v => { d.Work = v; c.Changed(); })));
        if (!SetupFlow.AsksStudy(d.Profile) && !SetupFlow.AsksWork(d.Profile))
            page.Children.Add(Ui.Section("", Ui.Caption(s["setup.studywork.none"], 13)));
        page.Children.Add(Ui.Section(s["notes.folders"],
            Ui.Field(s["notes.folders.hint"], string.Join("\n", d.NotesFolders), v =>
            {
                d.NotesFolders = v.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                c.Changed();
            }, multiline: true),
            Ui.Caption(s["notes.folders.note"])));
        page.Children.Add(Ui.Section(s["projects.folders"],
            Ui.Field(s["projects.folders.hint"], string.Join("\n", d.ProjectFolders), v =>
            {
                d.ProjectFolders = v.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                c.Changed();
            }, multiline: true),
            Ui.Field(s["brainstorm.folder"], d.BrainstormFolder, v => { d.BrainstormFolder = v.Trim(); c.Changed(); })));
        // The timetable: a calendar link (ICS) from the school or university, and the evening summary.
        page.Children.Add(Ui.Section(s["study.timetable"],
            Ui.Field(s["study.timetable.hint"], d.TimetableUrl, v => { d.TimetableUrl = v.Trim(); c.Changed(); }),
            Ui.Caption(s["study.timetable.note"])));
        var hour = Ui.Field(s["study.evening.hour"], d.EveningHour.ToString(), v =>
        {
            if (int.TryParse(v.Trim(), out var h) && h is >= 0 and <= 23) { d.EveningHour = h; c.Changed(); }
        });
        page.Children.Add(Ui.Section("",
            Ui.Switch(s["study.evening"], s["study.evening.why"], d.EveningSummary, v => { d.EveningSummary = v; c.Changed(); }),
            hour));
        return page;
    }

    // ── Areas ───────────────────────────────────────────────────────────────────────────────

    public static UIElement Areas(PageContext c)
    {
        var s = c.S;
        var d = c.Draft;
        var list = new StackPanel();
        for (int i = 0; i < d.Areas.Count; i++)
        {
            int index = i;
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new TextBox { Text = d.Areas[i], Margin = new Thickness(0, 0, 8, 0) };
            if (d.Areas[i] == d.ActiveArea) box.BorderBrush = Ui.Brush("Accent");
            box.TextChanged += (_, _) =>
            {
                bool wasActive = d.ActiveArea == d.Areas[index];
                d.Areas[index] = box.Text;
                if (wasActive) d.ActiveArea = box.Text;
                c.Changed();
            };
            grid.Children.Add(box);
            var use = Ui.Button(s["setup.areas.use"], () => { d.ActiveArea = d.Areas[index]; c.Changed(); c.Redraw(); });
            use.IsEnabled = d.Areas[i] != d.ActiveArea;
            Grid.SetColumn(use, 1);
            grid.Children.Add(use);
            var remove = Ui.Button(s["setup.areas.remove"], () =>
            {
                string gone = d.Areas[index];
                d.Areas.RemoveAt(index);
                if (d.ActiveArea == gone) d.ActiveArea = d.Areas[0];
                c.Changed();
                c.Redraw();
            });
            remove.IsEnabled = d.Areas.Count > 1;
            remove.Margin = new Thickness(0);
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);
            list.Children.Add(grid);
        }
        var name = new TextBox { Width = 260, Margin = new Thickness(0, 0, 8, 0) };
        void AddArea()
        {
            string n = name.Text.Trim();
            if (n.Length == 0 || d.Areas.Contains(n, StringComparer.OrdinalIgnoreCase)) return;
            d.Areas.Add(n);
            c.Changed();
            c.Redraw();
        }
        name.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) AddArea(); };
        // Phase 5: the hours of each area, one line each ("Lavoro: lun-ven 9-18"), and the morning briefing.
        bool it = s.Language == "it";
        var bad = Ui.Caption("", 12, new Thickness(0, 6, 0, 0));
        bad.Foreground = Ui.Brush("Warn");
        bad.Visibility = Visibility.Collapsed;
        var hours = Ui.Field(s["areas.schedule.hint"], string.Join("\n", d.AreaSchedule.Select(h => h.Format(it))), v =>
        {
            var lines = v.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var good = new List<AreaHours>();
            var wrong = new List<string>();
            foreach (var line in lines)
            {
                // The area must be one of the list (any case): "lavoro" becomes "Lavoro".
                if (AreaHours.Parse(line) is { } h && d.Areas.FirstOrDefault(a => a.Equals(h.Area, StringComparison.OrdinalIgnoreCase)) is string area)
                    good.Add(new AreaHours { Area = area, Days = h.Days, From = h.From, To = h.To });
                else wrong.Add(line);
            }
            d.AreaSchedule = good;
            bad.Text = s.Format("areas.schedule.bad", string.Join("; ", wrong));
            bad.Visibility = wrong.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            c.Changed();
        }, multiline: true);
        return Ui.Stack(
            Ui.Intro(s["setup.step.Areas"], s["setup.areas.text"]),
            Ui.Section(s.Format("setup.areas.active", d.ActiveArea), list,
                Ui.Caption(s["setup.areas.new"], 12, new Thickness(0, 12, 0, 4)), Ui.Row(name, Ui.Button(s["setup.areas.add"], AddArea)),
                Ui.Caption(s["setup.areas.note"], 12, new Thickness(0, 10, 0, 0))),
            Ui.Section(s["areas.schedule"], hours, bad),
            Ui.Section("", Ui.Switch(s["areas.briefing"], s.Format("areas.briefing.why", d.BriefingFrom, d.BriefingUntil), d.MorningBriefing,
                v => { d.MorningBriefing = v; c.Changed(); })));
    }

    // ── Accounts (phase 5) ──────────────────────────────────────────────────────────────────

    public static UIElement Accounts(PageContext c)
    {
        var s = c.S;
        var d = c.Draft;
        bool it = s.Language == "it";
        var set = c.Assistant.Accounts;
        var page = Ui.Stack(Ui.Intro(s["accounts.title"], s["accounts.text"]));
        if (set is null) return page;

        UIElement Provider(string id, string name)
        {
            var client = set.Client(id)!;
            var status = Ui.Status(client.Connected, client.Connected ? s.Format("accounts.connected", client.AccountName.Length > 0 ? client.AccountName : name) : s["accounts.notconnected"]);
            var note = Ui.Caption("", 12, new Thickness(0, 6, 0, 0));
            var connect = Ui.Button(s["accounts.connect"], () => { }, primary: true);
            connect.Click += async (_, _) =>
            {
                c.Changed();
                c.Flush();
                connect.IsEnabled = false;
                note.Text = s["accounts.browser"];
                string? problem = await set.ConnectAsync(id, url => AL76.Platform.WindowsSetup.Open(url), it, CancellationToken.None);
                if (problem is null && d.MailAccount.Length == 0) { d.MailAccount = id; c.Changed(); }
                if (problem is not null) { note.Text = problem; connect.IsEnabled = true; } else c.Redraw();
            };
            var disconnect = Ui.Button(s["accounts.disconnect"], () =>
            {
                set.Disconnect(id);
                if (d.MailAccount == id) d.MailAccount = "";
                c.Changed();
                c.Redraw();
            });
            var fields = new List<UIElement>
            {
                Ui.Field(s["accounts.clientid"], id == "microsoft" ? d.MicrosoftClientId : d.GoogleClientId, v =>
                {
                    if (id == "microsoft") d.MicrosoftClientId = v; else d.GoogleClientId = v;
                    c.Changed();
                }),
            };
            if (id == "google")
                fields.Add(Ui.Field(s["accounts.secret"], d.GoogleClientSecret, v => { d.GoogleClientSecret = v; c.Changed(); }));
            var children = new List<UIElement> { status };
            children.AddRange(fields);
            children.Add(Ui.Row(client.Connected ? disconnect : connect));
            children.Add(note);
            children.Add(Ui.Caption(s["accounts.guide." + id], 12, new Thickness(0, 10, 0, 0)));
            return Ui.Section(name, children.ToArray());
        }
        page.Children.Add(Provider("microsoft", "Microsoft (Outlook, To Do)"));
        page.Children.Add(Provider("google", "Google (Calendar, Tasks, Gmail)"));

        // Where each area lives.
        var areas = new StackPanel();
        foreach (var area in d.Areas)
        {
            var source = d.AreaSources.FirstOrDefault(x => x.Area.Equals(area, StringComparison.OrdinalIgnoreCase));
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            foreach (var w in new[] { 130.0, 170, 170, 170 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            var label = Ui.Label(area, strong: true);
            label.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(label);
            var where = new ComboBox { Margin = new Thickness(0, 0, 8, 0) };
            foreach (var (key, text) in new[] { ("local", s["accounts.area.local"]), ("microsoft", "Microsoft"), ("google", "Google") })
                where.Items.Add(new ComboBoxItem { Content = text, Tag = key });
            where.SelectedIndex = source?.Provider switch { "microsoft" => 1, "google" => 2, _ => 0 };
            var calendar = new TextBox { Text = source?.Calendar ?? "", Margin = new Thickness(0, 0, 8, 0), ToolTip = s["accounts.area.calendar"] };
            var list = new TextBox { Text = source?.TaskList ?? "", Margin = new Thickness(0, 0, 8, 0), ToolTip = s["accounts.area.list"] };
            void Store()
            {
                string provider = (where.SelectedItem as ComboBoxItem)?.Tag as string ?? "local";
                d.AreaSources.RemoveAll(x => x.Area.Equals(area, StringComparison.OrdinalIgnoreCase));
                if (provider != "local") d.AreaSources.Add(new AreaSource { Area = area, Provider = provider, Calendar = calendar.Text, TaskList = list.Text });
                calendar.IsEnabled = list.IsEnabled = provider != "local";
                c.Changed();
            }
            calendar.IsEnabled = list.IsEnabled = where.SelectedIndex > 0;
            where.SelectionChanged += (_, _) => Store();
            calendar.TextChanged += (_, _) => Store();
            list.TextChanged += (_, _) => Store();
            Grid.SetColumn(where, 1); Grid.SetColumn(calendar, 2); Grid.SetColumn(list, 3);
            grid.Children.Add(where); grid.Children.Add(calendar); grid.Children.Add(list);
            areas.Children.Add(grid);
        }
        page.Children.Add(Ui.Section(s["accounts.areas"], Ui.Caption(s["accounts.areas.note"], 12, new Thickness(0, 0, 0, 6)), areas));

        var mail = new StackPanel();
        foreach (var (key, text) in new[] { ("", s["accounts.mail.none"]), ("microsoft", "Microsoft"), ("google", "Google") })
        {
            var client = key.Length == 0 ? null : set.Client(key);
            mail.Children.Add(Ui.Radio("mail", text, client is { Connected: false } ? s["accounts.notconnected"] : "", d.MailAccount == key,
                () => { d.MailAccount = key; c.Changed(); }, enabled: client is null || client.Connected));
        }
        page.Children.Add(Ui.Section(s["accounts.mail"], mail, Ui.Caption(s["accounts.mail.note"], 12, new Thickness(0, 6, 0, 0))));
        return page;
    }

    // ── Brain ───────────────────────────────────────────────────────────────────────────────

    public static UIElement Brain(PageContext c)
    {
        var s = c.S;
        var d = c.Draft;
        var choices = new StackPanel();
        Dictionary<string, BrainStatus>? statuses = null;

        void Draw()
        {
            choices.Children.Clear();
            choices.Children.Add(Ui.Radio("brain", s["brain.auto.title"], s["brain.auto.detail"], d.Brain == "", () => Choose("")));
            foreach (var o in BrainCatalog.All)
            {
                BrainStatus? st = statuses is not null && statuses.TryGetValue(o.Id, out var x) ? x : null;
                bool usable = o.Id == "local" ? c.LocalModel.Length > 0 && st is not BrainStatus.NotInstalled : st is not null and not BrainStatus.NotInstalled;
                var block = new StackPanel();
                block.Children.Add(Ui.Radio("brain", o.Id == "local" ? s["brain.local.title"] : o.Title, s[o.DetailKey], d.Brain == o.Id,
                    () => Choose(o.Id), enabled: usable || d.Brain == o.Id));
                var inner = new StackPanel { Margin = new Thickness(28, -2, 0, 8) };
                if (st is null) inner.Children.Add(Ui.Caption(s["brain.checking"]));
                else
                {
                    string key = BrainCatalog.StatusKey(o.Id, st.Value, c.LocalModel);
                    string arg = o.Id == "local" ? c.LocalModel : c.Name;
                    inner.Children.Add(Ui.Status(st == BrainStatus.Ready, s.Format(key, arg)));
                    AddFix(inner, o, st.Value);
                }
                block.Children.Add(inner);
                choices.Children.Add(block);
            }
            if (d.Brain is "claude" or "chatgpt" or "gemini")
                choices.Children.Add(Ui.Field(s["brain.model"] + " · " + s["brain.model.hint"], d.BrainModel, v => { d.BrainModel = v.Trim(); c.Changed(); }));
        }

        void Choose(string id)
        {
            if (d.Brain == id) return;
            d.Brain = id;
            d.BrainModel = "";
            c.Changed();
            Draw();
        }

        void AddFix(StackPanel inner, BrainOption o, BrainStatus st)
        {
            var fix = BrainCatalog.FixFor(o.Id, st, c.LocalModel);
            if (fix == BrainCatalog.Fix.None) return;
            string line = fix switch
            {
                BrainCatalog.Fix.Install => o.Install,
                BrainCatalog.Fix.Login or BrainCatalog.Fix.PullModel => BrainCatalog.LoginScript(o.Id, ExecutableOf(c.Assistant, o.Id), c.LocalModel),
                _ => "",
            };
            string label = fix switch
            {
                BrainCatalog.Fix.Install => s["brain.install"],
                BrainCatalog.Fix.Login => s["brain.login"],
                BrainCatalog.Fix.PullModel => s["brain.pull"],
                _ => s.Format("brain.prepare", c.Name),
            };
            var row = Ui.Row();
            row.Children.Add(Ui.Button(label, () =>
            {
                try
                {
                    if (fix == BrainCatalog.Fix.Prepare)
                    {
                        // The user's own agy settings, changed only on this button (as on the Mac).
                        GeminiBrain.AllowTools();
                        Log.Info("cervello: Antigravity preparato per AL-76 dall'utente");
                        _ = Refresh();
                    }
                    else if (line.Length > 0)
                    {
                        Log.Info($"cervello: {label} {o.Title} in PowerShell");
                        WindowsSetup.RunInPowerShell(line, $"{c.Name} · {o.Title}");
                    }
                }
                catch (Exception e) { MessageBox.Show(s.Format("brain.failed", e.Message), c.Name); }
            }, primary: true));
            row.Children.Add(Ui.Link(s["brain.page"], () => WindowsSetup.Open(o.Page)));
            inner.Children.Add(row);
            if (fix == BrainCatalog.Fix.Prepare) inner.Children.Add(Ui.Caption(s.Format("brain.gemini.note", c.Name)));
            else if (line.Length > 0) inner.Children.Add(Ui.Command(line));
        }

        async Task Refresh()
        {
            statuses = null;
            Draw();
            try { statuses = await c.Assistant.StatusesAsync(); }
            catch (Exception e) { Log.Info($"cervello: controllo fallito: {e.Message}"); statuses = []; }
            Draw();
        }

        Draw();
        _ = Refresh();
        return Ui.Stack(
            Ui.Intro(s["setup.step.Brain"], s["setup.brain.text"]),
            // "Senza IA" (Mac 1.30.84): no model at all, only the commands AL-76 knows.
            Ui.Section("", Ui.Switch(s["brain.noai"], s["brain.noai.note"], d.NoAI, v => { d.NoAI = v; c.Changed(); })),
            Ui.Section("", choices, Ui.Row(Ui.Button(s["brain.recheck"], () => _ = Refresh())), Ui.Caption(s["brain.console.note"])),
            Ui.Section("", Ui.Caption(s["brain.privacy"], 12, new Thickness(0))));
    }

    public static string? ExecutableOf(Assistant a, string id) => a.Brains.FirstOrDefault(b => b.Id == id) switch
    {
        ClaudeBrain b => b.Executable,
        CodexBrain b => b.Executable,
        GeminiBrain b => b.Executable,
        _ => null,
    };

    // ── Permissions ─────────────────────────────────────────────────────────────────────────

    public static UIElement Permissions(PageContext c)
    {
        var s = c.S;
        var mic = new ContentControl();
        var notify = new ContentControl();
        void Refresh()
        {
            mic.Content = ConsentLine(s, WindowsSetup.Microphone());
            notify.Content = ConsentLine(s, WindowsSetup.Notifications());
        }
        Refresh();
        UIElement Row(string title, string why, ContentControl state, string page) => Ui.Stack(
            Ui.Label(title, strong: true), Ui.Caption(why), state,
            Ui.Row(Ui.Button(s["perm.open"], () => WindowsSetup.OpenSettings(page))));

        var page = Ui.Stack(
            Ui.Intro(s["setup.step.Permissions"], s["setup.perm.text"]),
            Ui.Section("", Row(s["perm.mic"], s["perm.mic.why"], mic, "privacy-microphone")),
            Ui.Section("", Row(s["perm.notify"], s["perm.notify.why"], notify, "notifications")),
            Ui.Section("", Ui.Switch(s["perm.startup"], s["perm.startup.why"], c.Draft.StartWithWindows, v => { c.Draft.StartWithWindows = v; c.Changed(); })),
            Ui.Section("", Ui.Label(s["perm.control"], strong: true), Ui.Caption(s["perm.control.why"])));
        // Settings change outside AL-76: look again every two seconds while the page is open.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => Refresh();
        page.Loaded += (_, _) => timer.Start();
        page.Unloaded += (_, _) => timer.Stop();
        return page;
    }

    private static UIElement ConsentLine(Strings s, WindowsSetup.Consent consent) => consent switch
    {
        WindowsSetup.Consent.Allowed => Ui.Status(true, s["perm.ok"]),
        WindowsSetup.Consent.Denied => Ui.Status(false, s["perm.denied"]),
        _ => Ui.Status(false, s["perm.unknown"]),
    };

    // ── Voice and call ──────────────────────────────────────────────────────────────────────

    public static UIElement Voice(PageContext c)
    {
        var s = c.S;
        var d = c.Draft;
        string lang = s.Language;

        var engines = new StackPanel();
        foreach (var (id, key) in new[] { ("", "voice.engine.auto"), ("kokoro", "voice.engine.kokoro"), ("windows", "voice.engine.windows") })
            engines.Children.Add(Ui.Radio("engine", s[key], "", d.VoiceEngine == id, () => { d.VoiceEngine = id; c.Changed(); }));

        string current = lang == "it" ? d.KokoroVoiceIt : d.KokoroVoiceEn;
        var voices = new WrapPanel();
        foreach (var v in VoiceHost.KokoroVoices(lang))
        {
            var r = Ui.Radio("kokoro", v, "", v == current, () =>
            {
                if (lang == "it") d.KokoroVoiceIt = v; else d.KokoroVoiceEn = v;
                current = v;
                c.Changed();
                if (lang == c.RunningLanguage) c.Voice?.SetKokoroVoice(v);
            });
            r.Margin = new Thickness(0, 3, 20, 3);
            r.MinWidth = 110;
            voices.Children.Add(r);
        }
        var sample = Ui.Button(s["voice.sample"], () => _ = c.Voice?.SampleAsync(current, s.Format("voice.sample.text", c.Name)));
        sample.IsEnabled = c.Voice is not null && lang == c.RunningLanguage;

        var heard = new ContentControl
        {
            Content = c.Voice?.IsListening == true ? Ui.Label(s.Format("voice.try", d.EffectiveWakePhrase(lang)), strong: true) : Ui.Caption(s["voice.notready"], 13),
        };
        void OnHeard() => heard.Dispatcher.BeginInvoke(() => heard.Content = Ui.Status(true, s["voice.heard"]));

        // The voiceprint (Mac: Settings › Voce › Riconoscimento): who may give orders.
        var checks = new StackPanel();
        foreach (var (id, key) in new[] { ("off", "voiceprint.mode.off"), ("observe", "voiceprint.mode.observe"), ("on", "voiceprint.mode.on") })
            checks.Children.Add(Ui.Radio("voicecheck", s[key], s[key + ".detail"], d.VoiceCheck == id, () => { d.VoiceCheck = id; c.Changed(); }));
        var printState = new ContentControl();
        void ShowPrint() => printState.Content = c.Voice is null ? Ui.Caption(s["voice.notready"], 13)
            : c.Voice.VoicePrintCount > 0 ? Ui.Status(true, s.Format("voiceprint.has", c.Voice.VoicePrintCount)) : Ui.Caption(s["voiceprint.none"], 13);
        ShowPrint();
        async void Enroll(VoiceEnrollment.Kind kind)
        {
            if (c.Voice is not { } host) return;
            if (await host.StartEnrollmentAsync(kind) is string problem) { printState.Content = Ui.Status(false, problem); return; }
            var w = new VoiceEnrollWindow(host, s) { Owner = Window.GetWindow(printState) };
            w.Closed += (_, _) => ShowPrint();
            w.Show();
        }
        var enroll = Ui.Row(
            Ui.Button(s["voiceprint.record"], () => Enroll(VoiceEnrollment.Kind.First), primary: true),
            Ui.Button(s["voiceprint.more"], () => Enroll(VoiceEnrollment.Kind.More)),
            Ui.Button(s["voiceprint.commands"], () => Enroll(VoiceEnrollment.Kind.Commands)),
            Ui.Button(s["voiceprint.delete"], () => { c.Voice?.DeleteVoicePrint(); ShowPrint(); }));
        foreach (UIElement b in enroll.Children) b.IsEnabled = c.Voice is not null && lang == c.RunningLanguage;

        var page = Ui.Stack(
            Ui.Intro(s["setup.step.Voice"], s["setup.voice.text"]),
            Ui.Section(s["voice.call"],
                Ui.Field(s["voice.name"], d.AssistantName, v => { d.AssistantName = v; c.Changed(); }),
                Ui.Field(s["voice.call"], d.WakePhrase, v => { d.WakePhrase = v; c.Changed(); }),
                Ui.Caption(s["voice.call.note"])),
            Ui.Section(s["voice.engine"], engines),
            Ui.Section(s["voice.kokoro.voice"], voices, Ui.Row(sample)),
            Ui.Section(s["voiceprint.title"], Ui.Caption(s["voiceprint.text"], 13), checks, printState, enroll),
            Ui.Section("", heard));
        if (c.Voice is { } host)
        {
            page.Loaded += (_, _) => host.CallHeard += OnHeard;
            page.Unloaded += (_, _) => host.CallHeard -= OnHeard;
        }
        return page;
    }

    // ── Appearance (Mac: SettingsView .look) ────────────────────────────────────────────────

    public static UIElement Look(PageContext c)
    {
        var s = c.S;
        var d = c.Draft;
        var styles = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var id in new[] { "reattore", "satellite", "robottino" })
        {
            var r = Ui.Radio("nucleus", s["look.style." + id], "", d.NucleusStyle == id, () =>
            {
                d.NucleusStyle = id;
                c.Changed();
                c.Redraw();
            });
            r.Margin = new Thickness(0, 4, 24, 4);
            styles.Children.Add(r);
        }
        var page = Ui.Stack(
            Ui.Intro(s["settings.page.look"], s["look.intro"]),
            Ui.Section(s["look.nucleus"], styles, Ui.Caption(s["look.nucleus.note"]),
                Ui.Switch(s["look.backdrop"], s["look.backdrop.note"], d.NucleusBackdrop, v => { d.NucleusBackdrop = v; c.Changed(); })));
        if (d.NucleusStyle != "robottino") return page;
        if (!AppPaths.CompanionModel)
        {
            // Guided (a tester had no robot, 23/11): Sketchfab in the browser, then the zip from Downloads.
            var result = Ui.Caption("", 13);
            void Install()
            {
                string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                string? zip = CompanionModel.FindZip(downloads);
                if (zip is null)
                {
                    var pick = new Microsoft.Win32.OpenFileDialog { Filter = "Zip|*.zip", InitialDirectory = Directory.Exists(downloads) ? downloads : "" };
                    if (pick.ShowDialog() != true) return;
                    zip = pick.FileName;
                }
                string? problem = CompanionModel.Install(zip, AppPaths.Companion);
                Log.Info(problem is null ? $"robottino: modello installato da {Path.GetFileName(zip)}" : $"robottino: modello non installato: {problem}");
                if (problem is not null) { result.Text = s.Format("look.robot.failed", problem); return; }
                c.Changed();
                c.Redraw();
            }
            page.Children.Add(Ui.Section(s["look.robot"],
                Ui.Caption(s.Format("look.robot.missing", c.Name, AppPaths.Companion), 13),
                Ui.Caption(s["look.robot.steps"], 13),
                Ui.Row(Ui.Button(s["look.robot.get"], () => WindowsSetup.Open(CompanionModel.Page)),
                       Ui.Button(s["look.robot.install"], Install),
                       Ui.Button(s["look.robot.folder"], () =>
                       {
                           Directory.CreateDirectory(AppPaths.Companion);
                           WindowsSetup.Open(AppPaths.Companion);
                       })),
                result));
            return page;
        }
        var flames = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, key) in new[] { ("fuoco", "look.flames.fire"), ("ioniche", "look.flames.ion") })
        {
            var r = Ui.Radio("flames", s[key], "", d.RobotFlames == id, () => { d.RobotFlames = id; c.Changed(); });
            r.Margin = new Thickness(0, 4, 24, 4);
            flames.Children.Add(r);
        }
        page.Children.Add(Ui.Section(s["look.robot"],
            Ui.Switch(s["look.robot.large"], "", d.RobotLarge, v => { d.RobotLarge = v; c.Changed(); }),
            Ui.Label(s["look.robot.flames"]), flames,
            Ui.Switch(s["look.robot.walks"], "", d.RobotWalks, v => { d.RobotWalks = v; c.Changed(); }),
            Ui.Caption(s["look.robot.note"])));
        return page;
    }

    // ── Modules, confirmations, system ──────────────────────────────────────────────────────

    public static UIElement Modules(PageContext c)
    {
        var s = c.S;
        var d = c.Draft;
        var list = new StackPanel();
        foreach (var m in ModuleRegistry.All)
            list.Children.Add(Ui.Switch(s[m.NameKey], s[m.DescriptionKey], ModuleRegistry.IsEnabled(m, d.EnabledModules), on =>
            {
                d.EnabledModules.RemoveAll(id => id.Equals(m.Id, StringComparison.OrdinalIgnoreCase));
                if (on) d.EnabledModules.Add(m.Id);
                c.Changed();
                c.Redraw();
            }));
        var page = Ui.Stack(Ui.Intro(s["setup.step.Modules"], s["setup.modules.text"]), Ui.Section("", list));
        var secrets = c.Assistant.Accounts?.Secrets;
        // News and the TV are not modules: always here (Mac: Impostazioni › Notizie, tv.json).
        page.Children.Add(Ui.Section(s["news.section"],
            Ui.Field(s["news.sources"], string.Join("\n", d.NewsSources), v =>
            {
                d.NewsSources = v.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                c.Changed();
            }, multiline: true),
            Ui.Field(s["news.topics"], string.Join("\n", d.NewsTopics), v =>
            {
                d.NewsTopics = v.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                c.Changed();
            }, multiline: true),
            Ui.Switch(s["news.briefing"], s["news.briefing.detail"], d.BriefingNews, v => { d.BriefingNews = v; c.Changed(); })));
        page.Children.Add(Ui.Section(s["tv.section"],
            Ui.Field(s["tv.host"], d.TvHost, v => { d.TvHost = v.Trim(); c.Changed(); }),
            Ui.Caption(s["tv.note"])));
        if (d.EnabledModules.Contains("maker", StringComparer.OrdinalIgnoreCase))
            page.Children.Add(MakerSection(c, secrets));
        if (d.EnabledModules.Contains("phone", StringComparer.OrdinalIgnoreCase))
            page.Children.Add(PhoneSection(c));
        if (d.EnabledModules.Contains("university", StringComparer.OrdinalIgnoreCase) && c.Assistant.Study.Portal is { } portal)
        {
            // The university's server and the student's credentials: the password goes straight to the encrypted store.
            var user = new TextBox { Margin = new Thickness(0, 4, 0, 6) };
            var password = new PasswordBox { Margin = new Thickness(0, 4, 0, 6) };
            var state = Ui.Status(portal.HasCredentials, portal.HasCredentials ? s["university.saved"] : s["university.missing"]);
            var note = Ui.Caption("", 12, new Thickness(0, 6, 0, 0));
            var save = Ui.Button(s["university.save"], () =>
            {
                if (user.Text.Trim().Length == 0 || password.Password.Length == 0) { note.Text = s["university.both"]; return; }
                c.Changed();
                c.Flush();
                portal.SaveCredentials(user.Text, password.Password);
                password.Clear();
                c.Redraw();
            }, primary: true);
            page.Children.Add(Ui.Section(s["module.university"],
                state,
                Ui.Field(s["university.name"], d.UniversityName, v => { d.UniversityName = v.Trim(); c.Changed(); }),
                Ui.Field(s["university.server"], d.UniversityServer, v => { d.UniversityServer = v.Trim(); c.Changed(); }),
                Ui.Field(s["university.agent"], d.UniversityAgent, v => { d.UniversityAgent = v.Trim(); c.Changed(); }),
                Ui.Label(s["university.user"]), user,
                Ui.Label(s["university.password"]), password,
                Ui.Row(save), note,
                Ui.Caption(s["university.note"], 12, new Thickness(0, 10, 0, 0))));
        }
        return page;
    }

    /// <summary>A secret typed once and kept encrypted: the box empties after saving, the state says whether it is there.</summary>
    private static UIElement[] SecretField(PageContext c, AL76.Core.Accounts.ISecretStore? secrets, string key, string label)
    {
        var s = c.S;
        var box = new PasswordBox { Margin = new Thickness(0, 4, 0, 6) };
        bool saved = (secrets?.Read(key) ?? "").Length > 0;
        var save = Ui.Button(s["secret.save"], () =>
        {
            if (secrets is null || box.Password.Length == 0) return;
            secrets.Write(key, box.Password);
            box.Clear();
            c.Redraw();
        });
        return [Ui.Label(label + (saved ? " · " + s["secret.saved"] : "")), box, Ui.Row(save)];
    }

    private static UIElement MakerSection(PageContext c, AL76.Core.Accounts.ISecretStore? secrets)
    {
        var s = c.S;
        var d = c.Draft;
        var items = new List<UIElement>
        {
            Ui.Field(s["maker.folders"], string.Join("\n", d.MakerFolders), v =>
            {
                d.MakerFolders = v.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                c.Changed();
            }, multiline: true),
            Ui.Field(s["maker.cli"], d.ArduinoCli, v => { d.ArduinoCli = v.Trim(); c.Changed(); }),
            Ui.Caption(s["maker.arduino.note"]),
            Ui.Field(s["maker.printer.host"], d.PrinterHost, v => { d.PrinterHost = v.Trim(); c.Changed(); }),
            Ui.Field(s["maker.printer.serial"], d.PrinterSerial, v => { d.PrinterSerial = v.Trim(); c.Changed(); }),
        };
        items.AddRange(SecretField(c, secrets, "bambu.code", s["maker.printer.code"]));
        items.Add(Ui.Caption(s["maker.printer.note"]));
        items.Add(Ui.Field(s["maker.ros"], d.RosBridgeUrl, v => { d.RosBridgeUrl = v.Trim(); c.Changed(); }));
        items.Add(Ui.Caption(s["maker.ros.note"]));
        items.Add(Ui.Field(s["maker.lab"], d.LabProject, v => { d.LabProject = v.Trim(); c.Changed(); }));
        items.Add(Ui.Field(s["maker.robot"], d.RobotConsoleUrl, v => { d.RobotConsoleUrl = v.Trim(); c.Changed(); }));
        items.AddRange(SecretField(c, secrets, "robotconsole.token", s["maker.robot.token"]));
        items.Add(Ui.Caption(s["maker.robot.note"]));
        return Ui.Section(s["module.maker"], items.ToArray());
    }

    private static UIElement PhoneSection(PageContext c)
    {
        var s = c.S;
        if (PhoneHost.Current is not { } phone) return Ui.Section(s["module.phone"], Ui.Caption(s["phone.restart"]));
        var items = new List<UIElement>
        {
            Ui.Status(phone.Connected, phone.Connected ? s["phone.connected"] : phone.Problem.Length > 0 ? phone.Problem : s["phone.waiting"]),
            Ui.Caption(s["phone.how"], 13, new Thickness(0, 8, 0, 6)),
        };
        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            var png = new QRCoder.PngByteQRCode(generator.CreateQrCode(phone.Link, QRCoder.QRCodeGenerator.ECCLevel.M)).GetGraphic(6);
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            items.Add(new System.Windows.Controls.Image { Source = image, Width = 200, Height = 200, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) });
        }
        catch (Exception e) { Log.Info($"telefono: QR non creato: {e.Message}"); }
        items.Add(Ui.Command(phone.Code));
        items.Add(Ui.Caption(s.Format("phone.address", phone.Address ?? Environment.MachineName), 12, new Thickness(0, 6, 0, 0)));
        items.Add(Ui.Row(Ui.Button(s["phone.renew"], () => { phone.Renew(); c.Redraw(); })));
        items.Add(Ui.Caption(s["phone.note"], 12, new Thickness(0, 10, 0, 0)));
        return Ui.Section(s["module.phone"], items.ToArray());
    }

    public static UIElement Confirmations(PageContext c)
    {
        var s = c.S;
        var d = c.Draft;
        return Ui.Stack(
            Ui.Intro(s["settings.page.confirm"], s["confirm.text"]),
            Ui.Section("",
                Ui.Radio("confirm", s["confirm.essential"], s["confirm.essential.detail"], !d.CarefulConfirmations, () => { d.CarefulConfirmations = false; c.Changed(); }),
                Ui.Radio("confirm", s["confirm.careful"], s["confirm.careful.detail"], d.CarefulConfirmations, () => { d.CarefulConfirmations = true; c.Changed(); })),
            Ui.Section(s["apps.control"],
                Ui.Switch(s["apps.control.switch"], s["apps.control.detail"], d.AppControl, v => { d.AppControl = v; c.Changed(); }),
                Ui.Caption(s["apps.control.never"])),
            Ui.Section(s["owner.check"],
                Ui.Switch(s["owner.check"], s["owner.check.detail"], d.OwnerCheck, v => { d.OwnerCheck = v; c.Changed(); })));
    }

    public static UIElement SystemPage(PageContext c)
    {
        var s = c.S;
        var d = c.Draft;
        var langs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, key) in new[] { ("", "lang.auto"), ("it", "lang.it"), ("en", "lang.en") })
        {
            var r = Ui.Radio("syslang", s[key], "", d.Language == id, () => { d.Language = id; c.Changed(); });
            r.Margin = new Thickness(0, 4, 24, 4);
            langs.Children.Add(r);
        }
        return Ui.Stack(
            Ui.Intro(s["settings.page.system"], ""),
            Ui.Section(s["setup.welcome.language"], langs, Ui.Caption(s.Format("lang.restart", c.Name))),
            Ui.Section("", Ui.Switch(s["perm.startup"], s["perm.startup.why"], d.StartWithWindows, v => { d.StartWithWindows = v; c.Changed(); })),
            Ui.Section("",
                Ui.Row(Ui.Button(s["system.setup"], c.OpenSetup), Ui.Button(s.Format("system.restart", c.Name), c.Restart)),
                Ui.Caption(s["system.restart.note"])),
            Ui.Section("", Ui.Row(Ui.Button(s["system.data"], () => WindowsSetup.Open(AppPaths.Data))), Ui.Caption(s["system.data.note"])));
    }

    // ── Done ────────────────────────────────────────────────────────────────────────────────

    public static UIElement Done(PageContext c, bool languageChanged)
    {
        var s = c.S;
        var lines = new StackPanel();
        lines.Children.Add(Ui.Caption(s["brain.checking"]));
        async Task Fill()
        {
            Dictionary<string, BrainStatus> statuses;
            try { statuses = await c.Assistant.StatusesAsync(); } catch { statuses = []; }
            lines.Children.Clear();
            bool mic = WindowsSetup.Microphone() != WindowsSetup.Consent.Denied;
            var copy = SetupFlow.Copy(c.Draft);
            SetupFlow.Normalize(copy);
            foreach (var l in SetupFlow.Summary(copy, s, statuses, mic)) lines.Children.Add(Ui.Status(l.Ok, l.Text));
        }
        _ = Fill();
        string call = c.Draft.EffectiveWakePhrase(s.Language);
        var examples = Ui.Stack(Ui.Label(s["setup.done.try"], strong: true));
        foreach (var key in new[] { "setup.done.ex1", "setup.done.ex2", "setup.done.ex3" })
        {
            var t = Ui.Label(s.Format(key, call));
            t.Foreground = Ui.Brush("Ice");
            t.Margin = new Thickness(0, 4, 0, 0);
            examples.Children.Add(t);
        }
        var page = Ui.Stack(Ui.Intro(s["setup.step.Done"], s["setup.done.text"]), Ui.Section("", lines), Ui.Section("", examples));
        if (languageChanged)
            page.Children.Add(Ui.Section("", Ui.Caption(s.Format("lang.restart", c.Name), 13, new Thickness(0)),
                Ui.Row(Ui.Button(s.Format("system.restart", c.Name), c.Restart))));
        return page;
    }
}
