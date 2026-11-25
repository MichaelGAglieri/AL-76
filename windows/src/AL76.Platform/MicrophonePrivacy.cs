using Microsoft.Win32;

namespace AL76.Platform;

/// <summary>
/// Windows' microphone privacy switches (Settings › Privacy &amp; security › Microphone). When one of them is off, the
/// microphone still opens but gives only silence, so AL-76 seems deaf (two testers' laptops, 25/11). Only read here:
/// the user turns them on, AL-76 opens the page.
/// </summary>
public static class MicrophonePrivacy
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>The settings page with the switches.</summary>
    public const string SettingsPage = "ms-settings:privacy-microphone";

    /// <summary>Which switch is off: "device" (microphone access for the whole PC), "user" (for this account),
    /// "desktop" (desktop apps), or null when nothing blocks it (or it cannot be read).</summary>
    public static string? Blocked()
    {
        try
        {
            if (Denied(Registry.LocalMachine, Store)) return "device";
            if (Denied(Registry.CurrentUser, Store)) return "user";
            if (Denied(Registry.CurrentUser, Store + @"\NonPackaged")) return "desktop";
            return null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static bool Denied(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue("Value") is string v && v.Equals("Deny", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens Settings at the microphone switches.</summary>
    public static void OpenSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SettingsPage) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
