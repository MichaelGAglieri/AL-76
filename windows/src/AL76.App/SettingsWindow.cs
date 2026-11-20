using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AL76.Core;
using AL76.Core.Config;
using AL76.Core.Hardware;
using AL76.Core.Localization;
using AL76.Core.Setup;

namespace AL76.App;

/// <summary>
/// Settings in pages, in the same style as the setup (Mac: SettingsView, a list of pages on the left).
/// Every change is saved half a second after the last edit, with no OK button, as on the Mac.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static readonly string[] PageIds = ["profile", "studywork", "areas", "accounts", "brain", "voice", "look", "confirm", "modules", "system"];

    private readonly AppConfig draft;
    private readonly PageContext context;
    private readonly ContentControl body = new();
    private readonly ScrollViewer scroller;
    private readonly TextBlock saved = new() { FontSize = 12, Margin = new Thickness(16, 0, 16, 14), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly DispatcherTimer saveLater = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string page = "profile";
    private readonly Action save;
    private readonly ListBox pages;

    public SettingsWindow(AppConfig live, Strings strings, Assistant assistant, VoiceHost? voice, TierDecision tier,
                          Action<AppConfig, bool> apply, Action openSetup, Action restart)
    {
        draft = SetupFlow.Copy(live);
        save = () =>
        {
            saveLater.Stop();
            apply(draft, false);
            saved.Text = "✓ " + strings["settings.saved"];
            saved.Foreground = Ui.Brush("Ok");
        };
        saveLater.Tick += (_, _) => save();
        context = new PageContext
        {
            S = strings, Draft = draft, Assistant = assistant, Voice = voice, Tier = tier, RunningLanguage = strings.Language,
            Changed = () => { saved.Text = ""; saveLater.Stop(); saveLater.Start(); },
            OpenSetup = () => { Flush(); Close(); openSetup(); },
            Restart = () => { Flush(); restart(); },
            Flush = () => Flush(),
        };
        context.Redraw = Draw;
        Closing += (_, _) => Flush();

        Title = strings.Format("settings.window", context.Name);
        Width = 860;
        Height = 640;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Brush("Back");
        Foreground = Ui.Brush("Text");
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 14;
        Ui.DarkTitleBar(this);

        var list = pages = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(10, 16, 10, 0) };
        foreach (var id in PageIds) list.Items.Add(new ListBoxItem { Content = strings["settings.page." + id], Tag = id });
        list.SelectedIndex = 0;
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: string id }) { page = id; Draw(); }
        };
        var side = new DockPanel { Width = 210, Background = Ui.Brush("Panel") };
        DockPanel.SetDock(saved, Dock.Bottom);
        side.Children.Add(saved);
        side.Children.Add(list);

        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Left);
        root.Children.Add(side);
        root.Children.Add(scroller = Ui.PageScroller(new Border { Padding = new Thickness(28, 22, 28, 16), Child = body }));
        Content = root;
        Draw();
    }

    /// <summary>Opens a page directly (the tray menu's "Cervello" goes to the brain page).</summary>
    public void Go(string id)
    {
        if (Array.IndexOf(PageIds, id) < 0) return;
        page = id;
        pages.SelectedIndex = Array.IndexOf(PageIds, id);
        Draw();
    }

    /// <summary>A change still waiting for its half second is saved now (closing, restarting).</summary>
    private void Flush()
    {
        if (saveLater.IsEnabled) save();
    }

    private void Draw()
    {
        body.Content = Page();
        scroller?.ScrollToTop();
    }

    private UIElement Page() => page switch
    {
        "profile" => Pages.You(context),
        "studywork" => Pages.StudyWork(context),
        "areas" => Pages.Areas(context),
        "accounts" => Pages.Accounts(context),
        "brain" => Pages.Brain(context),
        "voice" => Pages.Voice(context),
        "look" => Pages.Look(context),
        "confirm" => Pages.Confirmations(context),
        "modules" => Pages.Modules(context),
        _ => Pages.SystemPage(context),
    };
}
