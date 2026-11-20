using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AL76.Core.Localization;
using AL76.Core.Voice;

namespace AL76.App;

/// <summary>
/// The guided voice enrolment (Mac: SettingsView's "Registra la voce"): one sentence at a time in large type, what
/// AL-76 heard, and the progress. The microphone does the rest: each sentence said is checked and kept.
/// </summary>
public sealed class VoiceEnrollWindow : Window
{
    private readonly VoiceHost host;
    private readonly Strings s;
    private readonly TextBlock prompt = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBlock message = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
    private readonly ProgressBar progress = new() { Height = 6, Margin = new Thickness(0, 22, 0, 6), Maximum = 1 };
    private readonly TextBlock count = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };

    public VoiceEnrollWindow(VoiceHost host, Strings s)
    {
        this.host = host;
        this.s = s;
        Title = s["voiceprint.window"];
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = Ui.Brush("Panel");
        prompt.Foreground = Ui.Brush("Text");
        message.Foreground = Ui.Brush("Dim");
        count.Foreground = Ui.Brush("Dim");
        progress.Foreground = Ui.Brush("Accent");
        var stop = Ui.Button(s["voiceprint.stop"], Close);
        stop.HorizontalAlignment = HorizontalAlignment.Center;
        stop.Margin = new Thickness(0, 16, 0, 0);
        var stack = new StackPanel { Margin = new Thickness(28) };
        stack.Children.Add(Ui.Caption(s["voiceprint.read"], 13));
        stack.Children.Add(new Border { Child = prompt, Margin = new Thickness(0, 18, 0, 0), MinHeight = 90 });
        stack.Children.Add(message);
        stack.Children.Add(progress);
        stack.Children.Add(count);
        stack.Children.Add(stop);
        Content = stack;
        host.EnrollmentChanged += Changed;
        Show(host.Enrollment);
    }

    private void Changed(VoiceEnrollment? e) => Dispatcher.BeginInvoke(() => Show(e));

    private void Show(VoiceEnrollment? e)
    {
        if (e is null)
        {
            prompt.Text = s["voiceprint.done.title"];
            message.Text = s.Format("voiceprint.done", host.VoicePrintCount);
            progress.Value = 1;
            count.Text = "";
            return;
        }
        prompt.Text = e.Prompt;
        message.Text = e.Message;
        progress.Value = e.Prompts.Length == 0 ? 0 : (double)e.Step / e.Prompts.Length;
        count.Text = s.Format("voiceprint.step", e.Step + 1, e.Prompts.Length);
    }

    protected override void OnClosed(EventArgs e)
    {
        host.EnrollmentChanged -= Changed;
        host.CancelEnrollment();
        base.OnClosed(e);
    }
}
