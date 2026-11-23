using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AL76.Core;
using AL76.Core.Config;
using AL76.Core.Diagnostics;
using AL76.Core.Localization;
using AL76.Core.Modules;
using AL76.Core.Study;

namespace AL76.App;

/// <summary>
/// The Plancia (Mac 1.30.81–1.30.84): one window in the style of the settings instead of scattered panels.
/// Dialogo (the exchanges and a field to write), Giornata (sessions and reports), Stato (brain, "Senza IA",
/// the share of requests answered without a model and what went to the model, modules), Pensiero (the brain's
/// steps). Read-only except the field and the "Senza IA" switch; it refreshes itself every few seconds.
/// </summary>
public sealed class PlanciaWindow : Window
{
    private static readonly string[] SectionIds = ["dialog", "day", "status", "thought"];
    private readonly Assistant assistant;
    private readonly AppConfig config;
    private readonly Strings s;
    private readonly Action<string> say;
    private readonly ContentControl body = new();
    private readonly ScrollViewer scroller;
    private readonly DockPanel writing;
    private readonly TextBox field;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(3) };
    private string section = "dialog";
    private string shown = "";

    public PlanciaWindow(Assistant assistant, AppConfig config, Strings strings, Action<string> say)
    {
        this.assistant = assistant;
        this.config = config;
        s = strings;
        this.say = say;
        Title = strings["plancia.title"];
        Width = 820;
        Height = 600;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Brush("Back");
        Foreground = Ui.Brush("Text");
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 14;
        Ui.DarkTitleBar(this);

        var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(10, 16, 10, 0) };
        foreach (var id in SectionIds) list.Items.Add(new ListBoxItem { Content = strings["plancia." + id], Tag = id });
        list.SelectedIndex = 0;
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: string id }) { section = id; shown = ""; Draw(); }
        };
        var side = new DockPanel { Width = 190, Background = Ui.Brush("Panel") };
        side.Children.Add(list);

        field = new TextBox { MinHeight = 30, Padding = new Thickness(8, 4, 8, 4), VerticalContentAlignment = VerticalAlignment.Center };
        field.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Send(); } };
        var send = Ui.Button(strings["plancia.send"], Send, primary: true);
        send.Margin = new Thickness(8, 0, 0, 0);
        writing = new DockPanel { Margin = new Thickness(28, 8, 28, 14) };
        DockPanel.SetDock(send, Dock.Right);
        writing.Children.Add(send);
        writing.Children.Add(field);

        var main = new DockPanel();
        DockPanel.SetDock(writing, Dock.Bottom);
        main.Children.Add(writing);
        main.Children.Add(scroller = Ui.PageScroller(new Border { Padding = new Thickness(28, 22, 28, 16), Child = body }));

        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Left);
        root.Children.Add(side);
        root.Children.Add(main);
        Content = root;

        refresh.Tick += (_, _) => Draw();
        Loaded += (_, _) => refresh.Start();
        Closed += (_, _) => refresh.Stop();
        Draw();
    }

    private async void Send()
    {
        string text = field.Text.Trim();
        if (text.Length == 0) return;
        field.Text = "";
        field.IsEnabled = false;
        try
        {
            var answer = await assistant.HandleAsync(text, s.Language, "scritto");
            if (answer.Text.Length > 0 && !answer.Spoken) say(answer.Text);
        }
        catch (Exception e) { Log.Info($"plancia: richiesta fallita: {e.Message}"); }
        finally
        {
            field.IsEnabled = true;
            field.Focus();
            shown = "";
            Draw();
        }
    }

    /// <summary>Redrawn only when what it shows changed, so the scroll position stays while reading.</summary>
    private void Draw()
    {
        writing.Visibility = section == "dialog" ? Visibility.Visible : Visibility.Collapsed;
        var (content, signature) = section switch
        {
            "day" => Day(),
            "status" => Status(),
            "thought" => Thought(),
            _ => Dialog(),
        };
        if (signature == shown) return;
        bool atEnd = scroller.VerticalOffset >= scroller.ScrollableHeight - 4;
        shown = signature;
        body.Content = content;
        if (section is "dialog" or "thought" && atEnd) Dispatcher.BeginInvoke(scroller.ScrollToEnd, DispatcherPriority.Background);
    }

    private (UIElement, string) Dialog()
    {
        var all = assistant.Exchanges;
        var panel = Ui.Stack(Ui.Intro(s["plancia.dialog"], ""));
        if (all.Count == 0) panel.Children.Add(Ui.Caption(s["plancia.dialog.empty"], 13));
        foreach (var (at, user, reply, by) in all)
            panel.Children.Add(Ui.Section("", Ui.Label($"{at:HH:mm}  {user}", strong: true), Ui.Caption(reply, 13), Ui.Caption(by, 11)));
        return (panel, "d" + all.Count + (all.Count > 0 ? all[^1].at.Ticks : 0));
    }

    private (UIElement, string) Day()
    {
        var now = DateTime.Now;
        bool it = s.Language == "it";
        var tracker = assistant.Study.Activity;
        var sessions = tracker.All;
        var open = tracker.Open;
        var panel = Ui.Stack(Ui.Intro(s["plancia.day"], open is null ? s["plancia.session.none"]
            : s.Format("plancia.session.now", open.Name, ActivityTracker.Spoken(open.Length(now), it))));

        var today = sessions.Where(x => x.Start.Date == now.Date).OrderBy(x => x.Start).ToList();
        var rows = Ui.Stack();
        if (today.Count == 0) rows.Children.Add(Ui.Caption(s["plancia.sessions.none"], 13));
        foreach (var x in today)
            rows.Children.Add(Ui.Caption($"{x.Start:HH:mm}–{(x.End is DateTime e ? e.ToString("HH:mm") : "…")}  {x.Name}, {ActivityTracker.Spoken(x.Length(now), it)}"
                                         + (x.PausedSeconds >= 60 ? (it ? $" (pause {ActivityTracker.Spoken(TimeSpan.FromSeconds(x.PausedSeconds), true)})" : $" (breaks {ActivityTracker.Spoken(TimeSpan.FromSeconds(x.PausedSeconds), false)})") : ""), 13));
        var total = TimeSpan.FromSeconds(today.Sum(x => x.Length(now).TotalSeconds));
        if (today.Count > 1) rows.Children.Add(Ui.Label((it ? "Totale: " : "Total: ") + ActivityTracker.Spoken(total, it), strong: true));
        panel.Children.Add(Ui.Section(s["plancia.sessions"], rows));

        var week = tracker.Totals(now.Date.AddDays(-6), now.Date.AddDays(1), null, now);
        var weekRows = Ui.Stack();
        foreach (var (name, time) in week.OrderByDescending(x => x.Value)) weekRows.Children.Add(Ui.Caption($"{name}: {ActivityTracker.Spoken(time, it)}", 13));
        if (week.Count == 0) weekRows.Children.Add(Ui.Caption(s["plancia.sessions.none"], 13));
        panel.Children.Add(Ui.Section(s["plancia.week"], weekRows));

        var reports = sessions.Where(x => x.Report.Length > 0).OrderByDescending(x => x.End ?? x.Start).Take(10).ToList();
        var reportRows = Ui.Stack();
        if (reports.Count == 0) reportRows.Children.Add(Ui.Caption(s["plancia.reports.none"], 13));
        foreach (var x in reports) reportRows.Children.Add(Ui.Caption($"{x.Start:dd/MM}  {x.Name}: {x.Report}", 13));
        panel.Children.Add(Ui.Section(s["plancia.reports"], reportRows));
        return (panel, $"g{sessions.Count}{open?.Length(now).TotalMinutes:0}{reports.Count}");
    }

    private (UIElement, string) Status()
    {
        var now = DateTime.Now;
        var log = assistant.Requests;
        string brain = assistant.NoAI ? s["plancia.brain.off"]
            : s.Format("plancia.brain", assistant.ActiveBrainName ?? (assistant.ChosenBrain.Length > 0 ? assistant.ChosenBrain : "automatico"));
        var noAI = Ui.Switch(s["brain.noai"], s["brain.noai.note"], assistant.NoAI, v => { assistant.SetArtificialIntelligence(!v); shown = ""; Draw(); });
        var panel = Ui.Stack(Ui.Intro(s["plancia.status"], brain), Ui.Section("", noAI));

        var day = log.DayOf(now);
        var week = log.LastDays(7, now);
        var counts = Ui.Stack();
        if (week.Total == 0) counts.Children.Add(Ui.Caption(s["plancia.requests.none"], 13));
        else
        {
            if (day.Total > 0) counts.Children.Add(Ui.Label(s.Format("plancia.requests.today", day.Total, day.ModelFree ?? 0), strong: true));
            counts.Children.Add(Ui.Caption(s.Format("plancia.requests.week", week.Total, week.ModelFree ?? 0), 13));
            if (week.AverageModelSeconds is double avg) counts.Children.Add(Ui.Caption(s.Format("plancia.requests.avg", avg.ToString("0.0")), 13));
        }
        panel.Children.Add(Ui.Section(s["plancia.requests"], counts));

        var groups = log.ModelGroups(7, now);
        if (groups.Count > 0)
        {
            var rows = Ui.Stack();
            foreach (var g in groups.Take(8))
                rows.Children.Add(Ui.Caption($"{g.Name} ({g.Count}): " + string.Join(" · ", g.Examples.Select(e => $"«{(e.Length > 50 ? e[..50] + "…" : e)}»")), 13));
            panel.Children.Add(Ui.Section(s["plancia.groups"], rows));
        }

        var modules = Ui.Stack();
        foreach (var m in ModuleRegistry.All)
            modules.Children.Add(Ui.Status(config.EnabledModules.Contains(m.Id), s[m.NameKey]));
        panel.Children.Add(Ui.Section(s["plancia.modules"], modules));
        return (panel, $"s{assistant.NoAI}{log.Entries.Count}{brain}{string.Join(",", config.EnabledModules)}");
    }

    private (UIElement, string) Thought()
    {
        var lines = assistant.Trace;
        var panel = Ui.Stack(Ui.Intro(s["plancia.thought"], ""));
        if (lines.Count == 0) panel.Children.Add(Ui.Caption(s["plancia.thought.empty"], 13));
        foreach (var (at, line) in lines) panel.Children.Add(Ui.Caption($"{at:HH:mm:ss}  {line}", 12));
        return (panel, "t" + lines.Count + (lines.Count > 0 ? lines[^1].at.Ticks : 0));
    }
}
