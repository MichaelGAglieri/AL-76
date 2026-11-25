namespace AL76.Linux.Platform;

public enum DisplayServer { X11, Wayland, None }
public enum DesktopKind { Gnome, Kde, Xfce, Other }

/// <summary>
/// What AL-76 is running on: Wayland or X11, and which desktop. Every Linux feature that differs between them
/// (window control, keys, screenshots, hotkey, dark theme) asks this first (plan: "il quadro per desktop").
/// </summary>
public sealed record DesktopSession(DisplayServer Server, DesktopKind Desktop, string DesktopName, string Distribution)
{
    /// <summary>Xwayland is there to host AL-76's own windows (sphere, nucleus) on a Wayland session.</summary>
    public bool HasXwayland { get; init; }

    public static DesktopSession Detect(Func<string, string?>? env = null, string osRelease = "/etc/os-release")
    {
        env ??= Environment.GetEnvironmentVariable;
        string type = (env("XDG_SESSION_TYPE") ?? "").ToLowerInvariant();
        bool wayland = type == "wayland" || !string.IsNullOrEmpty(env("WAYLAND_DISPLAY"));
        bool x11 = !string.IsNullOrEmpty(env("DISPLAY"));
        var server = wayland ? DisplayServer.Wayland : x11 || type == "x11" ? DisplayServer.X11 : DisplayServer.None;
        string desktop = env("XDG_CURRENT_DESKTOP") ?? env("DESKTOP_SESSION") ?? "";
        return new DesktopSession(server, Kind(desktop), desktop, ReadDistribution(osRelease))
        {
            HasXwayland = wayland && x11,
        };
    }

    /// <summary>"ubuntu:GNOME", "KDE", "XFCE"... (XDG_CURRENT_DESKTOP is a colon-separated list).</summary>
    public static DesktopKind Kind(string desktop)
    {
        var parts = desktop.ToUpperInvariant().Split(':');
        if (parts.Any(p => p.Contains("GNOME") || p == "UNITY")) return DesktopKind.Gnome;
        if (parts.Any(p => p.Contains("KDE") || p.Contains("PLASMA"))) return DesktopKind.Kde;
        if (parts.Any(p => p.Contains("XFCE"))) return DesktopKind.Xfce;
        return DesktopKind.Other;
    }

    /// <summary>PRETTY_NAME of /etc/os-release ("Ubuntu 26.04.1 LTS").</summary>
    public static string ReadDistribution(string osRelease)
    {
        try
        {
            foreach (var line in File.ReadLines(osRelease))
                if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) return line[12..].Trim().Trim('"');
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return "Linux";
    }

}
