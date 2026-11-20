using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AL76.Core;
using AL76.Core.Config;
using AL76.Core.Hardware;
using AL76.Core.Localization;
using AL76.Core.Setup;

namespace AL76.App;

/// <summary>
/// The first-run setup window (Mac: OnboardingWindow, 680×600): step bar at the top, the page, and
/// Indietro / Avanti at the bottom. It edits a copy of the settings; each step is saved when you go on, so
/// closing halfway keeps what was written. Choosing a language redraws the setup in that language at once.
/// </summary>
public sealed class SetupWindow : Window
{
    private readonly AppConfig draft;
    private readonly Action<AppConfig, bool> apply;
    private readonly PageContext context;
    private readonly string startLanguage;
    private SetupStep step = SetupStep.Welcome;
    private readonly ContentControl body = new();
    private readonly ScrollViewer scroller;
    private readonly Grid header = new();
    private readonly DockPanel footer = new() { LastChildFill = false };

    /// <param name="apply">Copies the edited settings onto the live ones and saves (finished = the setup is done).</param>
    public SetupWindow(AppConfig live, Strings strings, Assistant assistant, VoiceHost? voice, TierDecision tier,
                       Action<AppConfig, bool> apply, Action restart)
    {
        this.apply = apply;
        draft = SetupFlow.Copy(live);
        startLanguage = strings.Language;
        context = new PageContext
        {
            S = strings, Draft = draft, Assistant = assistant, Voice = voice, Tier = tier, RunningLanguage = strings.Language,
            Restart = () => { apply(draft, true); restart(); },
        };
        context.Redraw = Draw;

        Width = 720;
        Height = 680;
        MinWidth = 600;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Brush("Back");
        Foreground = Ui.Brush("Text");
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 14;
        Ui.DarkTitleBar(this);

        var root = new DockPanel();
        var top = new Border { Padding = new Thickness(24, 18, 24, 8), Child = header };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var bottom = new Border { Padding = new Thickness(24, 14, 24, 14), Background = new SolidColorBrush(Color.FromArgb(8, 255, 255, 255)), Child = footer };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(scroller = Ui.PageScroller(new Border { Padding = new Thickness(24, 10, 24, 10), Child = body }));
        Content = root;
        Draw();
    }

    private Strings S => context.S;

    private void Draw()
    {
        Title = S.Format("setup.window", context.Name);
        DrawHeader();
        body.Content = step switch
        {
            SetupStep.Welcome => Pages.Welcome(context, LanguageChanged),
            SetupStep.You => Pages.You(context),
            SetupStep.StudyWork => Pages.StudyWork(context),
            SetupStep.Areas => Pages.Areas(context),
            SetupStep.Brain => Pages.Brain(context),
            SetupStep.Permissions => Pages.Permissions(context),
            SetupStep.Voice => Pages.Voice(context),
            SetupStep.Modules => Pages.Modules(context),
            _ => Pages.Done(context, LanguageChosen != startLanguage),
        };
        DrawFooter();
        scroller?.ScrollToTop();
    }

    private string LanguageChosen => draft.EffectiveLanguage(CultureInfo.CurrentUICulture.Name);

    private void LanguageChanged()
    {
        context.S = new Strings(LanguageChosen);
        SetupFlow.ProfileChanged(draft, LanguageChosen);
        Draw();
    }

    private void DrawHeader()
    {
        header.Children.Clear();
        header.ColumnDefinitions.Clear();
        int i = 0;
        foreach (var item in SetupFlow.Steps)
        {
            header.ColumnDefinitions.Add(new ColumnDefinition());
            bool reached = item <= step;
            var cell = new StackPanel { Margin = new Thickness(3, 0, 3, 0) };
            cell.Children.Add(new Rectangle { Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = reached ? Ui.Brush("Accent") : new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)) });
            cell.Children.Add(new TextBlock
            {
                Text = S["setup.step." + item], FontSize = 10, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = item == step ? FontWeights.SemiBold : FontWeights.Normal, Foreground = item == step ? Ui.Brush("Accent") : Ui.Brush("Dim"),
            });
            Grid.SetColumn(cell, i++);
            header.Children.Add(cell);
        }
    }

    private void DrawFooter()
    {
        footer.Children.Clear();
        if (step == SetupStep.Welcome) footer.Children.Add(Ui.Link(S["setup.skipall"], Finish));
        else footer.Children.Add(Ui.Button(S["setup.back"], () => Move(-1)));

        Button main = step switch
        {
            SetupStep.Welcome => Ui.Button(S["setup.start"], () => Move(1), primary: true),
            SetupStep.Done => Ui.Button(S["setup.finish"], Finish, primary: true),
            _ => Ui.Button(S["setup.next"], () => Move(1), primary: true),
        };
        main.IsDefault = true;
        main.Margin = new Thickness(0);
        DockPanel.SetDock(main, Dock.Right);
        footer.Children.Add(main);
        if (step is not (SetupStep.Welcome or SetupStep.Done))
        {
            var skip = Ui.Link(S["setup.skip"], () => Move(1));
            skip.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(skip, Dock.Right);
            footer.Children.Add(skip);
        }
    }

    /// <summary>Going on saves the step (Mac: every step is saved when you go on). Nothing is required, so
    /// "Salta" just goes on: an empty field is a skipped step.</summary>
    private void Move(int delta)
    {
        if (delta > 0) apply(draft, false);
        step = (SetupStep)Math.Clamp((int)step + delta, 0, SetupFlow.Steps.Count - 1);
        Draw();
    }

    private void Finish()
    {
        apply(draft, true);
        Close();
    }
}
