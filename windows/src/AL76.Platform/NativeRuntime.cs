using System.Runtime.InteropServices;

namespace AL76.Platform;

/// <summary>
/// whisper.cpp (Whisper.net) is built with MSVC and needs MSVCP140/VCRUNTIME140, which a fresh Windows does
/// not have (found in the new VM, 16/11/2026). The installer will carry them (phase 8); until then the app
/// says what is missing instead of a raw loader error.
/// </summary>
public static class NativeRuntime
{
    public static bool VcRuntimeInstalled =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "msvcp140.dll"))
        && File.Exists(Path.Combine(Environment.SystemDirectory, "vcruntime140.dll"));

    /// <summary>"arm64" or "x64", as in the vc_redist file names.</summary>
    public static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
}
