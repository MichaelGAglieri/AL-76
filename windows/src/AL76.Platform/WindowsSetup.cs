using System.Diagnostics;
using Microsoft.Win32;

namespace AL76.Platform;

/// <summary>
/// What the first-run setup checks and changes on Windows (Mac: the Permissions step): the microphone
/// switch in the privacy settings, notifications, starting with Windows, and the PowerShell window that
/// installs or signs in a brain's CLI.
/// </summary>
public static class WindowsSetup
{
    public enum Consent { Allowed, Denied, Unknown }

    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    /// <summary>
    /// The microphone switches of Settings › Privacy: the general one, and the one for desktop apps
    /// (NonPackaged), which is the one that applies to AL-76.
    /// </summary>
    public static Consent Microphone()
    {
        string? general = Read(Registry.CurrentUser, ConsentStore + "microphone") ?? Read(Registry.LocalMachine, ConsentStore + "microphone");
        string? desktop = Read(Registry.CurrentUser, ConsentStore + @"microphone\NonPackaged");
        if (general == "Deny" || desktop == "Deny") return Consent.Denied;
        if (general == "Allow" && desktop is null or "Allow") return Consent.Allowed;
        return general is null && desktop is null ? Consent.Unknown : Consent.Allowed;
    }

    /// <summary>Notifications on for the whole PC (the toast switch of Settings › Notifications).</summary>
    public static Consent Notifications()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
        return key?.GetValue("ToastEnabled") is int v && v == 0 ? Consent.Denied : Consent.Allowed;
    }

    private static string? Read(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue("Value") as string;
    }

    /// <summary>The page of Windows Settings for a permission.</summary>
    public static void OpenSettings(string page) => Open($"ms-settings:{page}");

    public static void Open(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "AL-76";

    /// <summary>True inside the MSIX package (Store, winget): there the Run key is private to the package and
    /// start-up goes through the manifest's StartupTask "AL76Startup".</summary>
    public static bool Packaged
    {
        get
        {
            try { return Windows.ApplicationModel.Package.Current is not null; }
            catch (InvalidOperationException) { return false; }
            catch (System.Runtime.InteropServices.COMException) { return false; }
        }
    }

    public static bool StartsWithWindows()
    {
        if (Packaged)
        {
            var task = Windows.ApplicationModel.StartupTask.GetAsync("AL76Startup").AsTask().GetAwaiter().GetResult();
            return task.State is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        }
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunName) is string;
    }

    /// <summary>Starts AL-76 at sign-in: the user's Run key (no administrator rights), or the package's StartupTask
    /// (Windows may refuse if the user turned it off in Task Manager: then it stays off).</summary>
    public static void SetStartWithWindows(bool on)
    {
        if (Packaged)
        {
            var task = Windows.ApplicationModel.StartupTask.GetAsync("AL76Startup").AsTask().GetAwaiter().GetResult();
            if (on) task.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
            else task.Disable();
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunName, throwOnMissingValue: false);
    }

    /// <summary>
    /// A PowerShell window the user sees, running one line (an official installer or a sign-in). It stays open
    /// at the end, so the user reads how it went; AL-76 does not read or type into it.
    /// </summary>
    public static void RunInPowerShell(string line, string title)
    {
        string script = $"$Host.UI.RawUI.WindowTitle = '{title.Replace("'", "''")}'; Write-Host '> {line.Replace("'", "''")}' -ForegroundColor Cyan; {line}";
        // AL-76 has no console of its own, so the child gets a new, visible one.
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = false };
        foreach (var a in new[] { "-NoExit", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script }) info.ArgumentList.Add(a);
        Process.Start(info)?.Dispose();
    }
}
