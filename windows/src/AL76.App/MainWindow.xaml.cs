using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AL76.Core;
using AL76.Core.Config;
using AL76.Core.Hardware;
using AL76.Core.Localization;
using AL76.Core.Modules;

namespace AL76.App;

/// <summary>
/// Phase 0 window: shows what AL-76 measured on this PC and the models it would pick.
/// It will become the HUD in phase 3.
/// </summary>
public partial class MainWindow : Window
{
    public event System.Action? SettingsRequested;
    public event System.Action? SetupRequested;

    public MainWindow(AppConfig config, Strings s, HardwareInfo pc, TierDecision d)
    {
        InitializeComponent();
        Ui.DarkTitleBar(this);
        Title = $"{AppInfo.Name} {AppInfo.Version}";
        NameText.Text = config.AssistantName;
        Phase.Text = $"{s["app.phase"]} · v{AppInfo.Version}";
        Running.Text = s.Format("app.running", config.AssistantName);
        VoiceTitle.Text = s["voice.title"].ToUpperInvariant();
        VoiceStatus.Text = s["voice.loading"];
        if (d.BelowRequirements)
        {
            Warning.Visibility = Visibility.Visible;
            WarningText.Text = s["hw.below"];
        }

        var gpus = new List<string>();
        foreach (var g in pc.Gpus) gpus.Add(g.VramGB is double v ? $"{g.Name} ({v:0.#} GB)" : g.Name);

        AddSection(s["hw.title"],
            s.Format("hw.tier", d.Tier),
            s.Format("hw.ram", pc.RamGB),
            s.Format("hw.cores", pc.LogicalCores),
            s.Format("hw.arch", pc.Architecture),
            gpus.Count == 0 ? s["hw.gpu.none"] : s.Format("hw.gpu", string.Join(", ", gpus)));

        AddSection(s["models.title"],
            s.Format("models.wake", d.WakeModel),
            s.Format("models.command", d.CommandModel),
            s.Format("models.voice", d.VoiceEngine),
            d.LocalBrain is null ? s["models.brain.none"] : s.Format("models.brain", d.LocalBrain));

        AddSection(s["settings.title"],
            s.Format("settings.language", s.Language),
            s.Format("settings.wake", config.EffectiveWakePhrase(s.Language)));

        var modules = new List<string>();
        foreach (var m in ModuleRegistry.All)
            modules.Add($"{s[m.NameKey]} — {(ModuleRegistry.IsEnabled(m, config.EnabledModules) ? s["module.on"] : s["module.off"])}: {s[m.DescriptionKey]}");
        AddSection(s["modules.title"], modules.ToArray());
        Sections.Children.Add(Ui.Row(
            Ui.Button(s["tray.settings"].TrimEnd('…'), () => SettingsRequested?.Invoke(), primary: true),
            Ui.Button(s["tray.setup"].TrimEnd('…'), () => SetupRequested?.Invoke())));
    }

    private void Wheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var scroller = (ScrollViewer)sender;
        scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta * 0.6);
        e.Handled = true;
    }

    // The voice runs on other threads: these hop to the window's own.
    public void SetVoiceStatus(string text) => Dispatcher.BeginInvoke(() => VoiceStatus.Text = text);
    public void SetVoiceDetail(string text) => Dispatcher.BeginInvoke(() => VoiceDetail.Text = text);
    public void SetVoiceLast(string text) => Dispatcher.BeginInvoke(() => VoiceLast.Text = text);
    public void SetVoiceLevel(float rms) => Dispatcher.BeginInvoke(() => VoiceLevel.Value = rms);

    private void AddSection(string title, params string[] lines)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(), FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Accent"), Margin = new Thickness(0, 0, 0, 6),
        });
        foreach (var line in lines)
            panel.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
        Sections.Children.Add(new Border
        {
            Background = (Brush)FindResource("Panel"), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 12), Child = panel,
        });
    }
}
