using System.Runtime.InteropServices;
using System.Text;
using AL76.Core.Accounts;

namespace AL76.Platform;

/// <summary>
/// The accounts' tokens encrypted for the Windows user (DPAPI, the same protection as the browsers' saved data):
/// one file per name in %APPDATA%\AL-76\segreti. Another user, or a copy of the file on another PC, cannot read them.
/// </summary>
public sealed class WindowsSecrets(string folder) : ISecretStore
{
    private string File(string name) => Path.Combine(folder, string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_')) + ".bin");

    public string? Read(string name)
    {
        string file = File(name);
        if (!System.IO.File.Exists(file)) return null;
        try { return Encoding.UTF8.GetString(Unprotect(System.IO.File.ReadAllBytes(file))); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { return null; }
    }

    public void Write(string name, string? value)
    {
        string file = File(name);
        if (value is null) { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); return; }
        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllBytes(file + ".tmp", Protect(Encoding.UTF8.GetBytes(value)));
        System.IO.File.Move(file + ".tmp", file, overwrite: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);

    private const int UiForbidden = 1;

    private static byte[] Protect(byte[] data) => Call(data, true);
    private static byte[] Unprotect(byte[] data) => Call(data, false);

    private static byte[] Call(byte[] data, bool protect)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, "AL-76", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { handle.Free(); }
    }
}
