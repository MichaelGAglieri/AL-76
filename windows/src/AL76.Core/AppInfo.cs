namespace AL76.Core;

/// <summary>Name and version shown in the window, the logs and the tray menu.</summary>
public static class AppInfo
{
    public const string Name = "AL-76";
    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
