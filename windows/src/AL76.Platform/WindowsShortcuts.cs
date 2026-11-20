using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using AL76.Core.Diagnostics;

namespace AL76.Platform;

/// <summary>
/// Links to AL-76 for a copy that was only unpacked (not installed by the Store or the installer): the Start menu
/// entry is kept up to date at every start, the desktop one only on request. Inside the MSIX package Windows makes
/// its own entry, so nothing is done there.
/// </summary>
public static class WindowsShortcuts
{
    public static string Desktop => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AL-76.lnk");
    public static string StartMenu => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AL-76.lnk");

    /// <summary>At every start: the Start menu link points to this exe (it may have been moved or updated).</summary>
    public static void KeepStartMenu()
    {
        if (WindowsSetup.Packaged || Environment.ProcessPath is not { } exe) return;
        try
        {
            if (Target(StartMenu)?.Equals(exe, StringComparison.OrdinalIgnoreCase) == true) return;
            Create(StartMenu, exe);
            Log.Info("collegamento nel menu Start aggiornato");
        }
        catch (Exception e) { Log.Info($"collegamento nel menu Start non creato: {e.Message}"); }
    }

    /// <summary>The desktop link (tray menu "Collegamento sul desktop"); null = done, otherwise the reason.</summary>
    public static string? MakeDesktop()
    {
        if (Environment.ProcessPath is not { } exe) return "percorso dell'app sconosciuto";
        try
        {
            Create(Desktop, exe);
            Log.Info("collegamento sul desktop creato");
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    private static void Create(string link, string exe)
    {
        var shell = (IShellLinkW)new ShellLink();
        shell.SetPath(exe);
        shell.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
        shell.SetIconLocation(exe, 0);
        shell.SetDescription("AL-76");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        ((IPersistFile)shell).Save(link, true);
        Marshal.ReleaseComObject(shell);
    }

    private static string? Target(string link)
    {
        if (!File.Exists(link)) return null;
        var shell = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)shell).Load(link, 0);
            var path = new char[1024];
            shell.GetPath(path, path.Length, IntPtr.Zero, 0);
            return new string(path).TrimEnd('\0');
        }
        finally { Marshal.ReleaseComObject(shell); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] file, int max, IntPtr data, int flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
