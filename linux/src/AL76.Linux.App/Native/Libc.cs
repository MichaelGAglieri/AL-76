using System.Runtime.InteropServices;

namespace AL76.Linux.App.Native;

/// <summary>
/// The real process environment. On Linux Environment.SetEnvironmentVariable changes only .NET's own copy: GTK, WebKit
/// and the processes WebKit starts read the C one, so settings for them go through setenv.
/// </summary>
internal static partial class Libc
{
    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnv(string name, string value, int overwrite);

    /// <summary>Sets a variable unless the user already set it (their value wins).</summary>
    public static void Default(string name, string value)
    {
        if (Environment.GetEnvironmentVariable(name) is { Length: > 0 }) return;
        SetEnv(name, value, 0);
        Environment.SetEnvironmentVariable(name, value);
    }
}
